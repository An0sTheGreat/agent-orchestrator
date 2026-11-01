// Agent Orchestrator's bridge for Pi (orchestrator.py, engine "pi"). Loaded with `pi --mode rpc -e <this file>`.
// - Guards Pi's own file tools (read, edit, write): every path goes to the engine's gateway, which applies the same
//   folder grants and protected files as the built-in Code engine (code_path). A refused path blocks the call; an
//   accepted one is replaced by the absolute path the gateway checked, so Pi opens exactly that file.
// - Registers the engine's tools (list_files, search_files, todo, run_checks, delegate, web and utility tools) from
//   AO_TOOLS; each call runs in orchestrator.py through the gateway, so there is one implementation of each.
import type { ExtensionAPI } from "@earendil-works/pi-coding-agent";
import { Type } from "typebox";
import { readFileSync } from "node:fs";
import { request } from "node:http";

type Reply = { result?: string; error?: string };
type Spec = { name: string; description: string; parameters: Record<string, unknown> };

// tool -> [writes, the file must exist (Pi's read tries other spellings of a missing path)]
const GUARDED: Record<string, [boolean, boolean]> = { read: [false, true], edit: [true, true], write: [true, false] };

export default function (pi: ExtensionAPI) {
	const gateway = process.env.AO_GATEWAY;
	const token = process.env.AO_TOKEN ?? "";
	if (!gateway) return; // not started by orchestrator.py: nothing to bridge

	// node:http, not fetch: fetch gives up after 5 minutes without response headers, and a delegation can take longer.
	const call = (body: unknown, signal?: AbortSignal): Promise<Reply> =>
		new Promise((resolve, reject) => {
			const data = Buffer.from(JSON.stringify(body));
			const req = request(gateway, {
				method: "POST",
				headers: { "content-type": "application/json", "content-length": data.length, "x-ao-token": token },
				signal,
			}, (res) => {
				const chunks: Buffer[] = [];
				res.on("data", (chunk: Buffer) => chunks.push(chunk));
				res.on("end", () => {
					if (res.statusCode !== 200) return resolve({ error: `gateway HTTP ${res.statusCode}` });
					try {
						resolve(JSON.parse(Buffer.concat(chunks).toString("utf8")) as Reply);
					} catch {
						resolve({ error: "unreadable gateway reply" });
					}
				});
				res.on("error", reject);
			});
			req.on("error", reject);
			req.end(data);
		});

	const specs: Spec[] = process.env.AO_TOOLS ? JSON.parse(readFileSync(process.env.AO_TOOLS, "utf8")) : [];
	for (const spec of specs) {
		pi.registerTool({
			name: spec.name,
			label: spec.name,
			description: spec.description,
			parameters: Type.Unsafe(spec.parameters),
			async execute(_toolCallId, params, signal) {
				const reply = await call({ tool: spec.name, args: params }, signal);
				if (reply.error !== undefined) throw new Error(reply.error);
				return { content: [{ type: "text", text: reply.result ?? "" }], details: undefined };
			},
		});
	}

	pi.on("tool_call", async (event) => {
		if (!(event.toolName in GUARDED)) return undefined;
		const input = event.input as { path?: unknown };
		const [write, existing] = GUARDED[event.toolName];
		const reply = await call({ guard: typeof input.path === "string" ? input.path : "", write, existing });
		if (reply.error !== undefined || !reply.result) return { block: true, reason: reply.error ?? "path refused" };
		input.path = reply.result; // the checked absolute path: Pi's own path rewriting cannot turn it into another file
		return undefined;
	});
}
