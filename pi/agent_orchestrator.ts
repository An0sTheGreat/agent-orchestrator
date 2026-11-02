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

type Reply = { result?: string; error?: string; images?: string[] }; // images: data URLs (a browser screenshot)
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
				const images = (reply.images ?? []).flatMap((url) => {
					const m = /^data:(image\/[\w.+-]+);base64,(.*)$/s.exec(url);
					return m ? [{ type: "image" as const, mimeType: m[1], data: m[2] }] : [];
				});
				return { content: [{ type: "text" as const, text: reply.result ?? "" }, ...images], details: undefined };
			},
		});
	}

	// orchestrator.py's slim_tool_results for Pi: in each request, a tool result older than the last KEEP_TOOL_ROUNDS
	// assistant messages and longer than STUB_OVER characters becomes a one-line note. Pi's session keeps the full result.
	const KEEP_TOOL_ROUNDS = 2, STUB_OVER = 600;
	pi.on("context", async (event) => {
		const rounds = event.messages.flatMap((m, i) => (m.role === "assistant" ? [i] : []));
		if (rounds.length <= KEEP_TOOL_ROUNDS) return undefined;
		const keepFrom = rounds[rounds.length - KEEP_TOOL_ROUNDS];
		const calls = new Map<string, string>();
		for (const m of event.messages.slice(0, keepFrom))
			if (m.role === "assistant")
				for (const c of m.content) if (c.type === "toolCall") calls.set(c.id, JSON.stringify(c.arguments ?? {}).slice(0, 200));
		const messages = event.messages.map((m, i) => {
			if (i >= keepFrom || m.role !== "toolResult") return m;
			const text = m.content.map((c) => (c.type === "text" ? c.text : "")).join("");
			if (text.length <= STUB_OVER && m.content.some((c) => c.type !== "text")) // an older screenshot: the text stays, the image goes
				return { ...m, content: [{ type: "text" as const, text: `${text} (the image is left out to save context; take it again if you need it)` }] };
			if (text.length <= STUB_OVER) return m;
			const note = `[older result of ${m.toolName} ${calls.get(m.toolCallId) ?? ""} (${text.length.toLocaleString("en-US")} characters, ` +
				`${text.split("\n").length} lines) left out to save context; call the tool again if you need it]`;
			return { ...m, content: [{ type: "text" as const, text: note }] };
		});
		return { messages };
	});

	// Pi's read gets read_file's limits (orchestrator.py sends them): AO_READ_LINES lines unless the model sets a limit,
	// and a result cut at a line end to AO_READ_CHARS (read_file's share of the window), saying where to read on.
	const readLines = Number(process.env.AO_READ_LINES) || 0, readChars = Number(process.env.AO_READ_CHARS) || 0;
	pi.on("tool_result", async (event) => {
		if (event.toolName !== "read" || !readChars) return undefined;
		const text = event.content.map((c) => (c.type === "text" ? c.text : "")).join("");
		if (text.length <= readChars || event.content.some((c) => c.type !== "text")) return undefined;
		const head = text.slice(0, readChars), cut = head.lastIndexOf("\n");
		const start = Math.max(Number(event.input.offset) || 1, 1);
		const kept = cut > 0 ? head.slice(0, cut) : head;
		const next = cut > 0 ? `read on with offset=${start + kept.split("\n").length}`
			: `line ${start} alone is longer than this, so read parts of it another way`;
		return { content: [{ type: "text", text: `${kept}\n... cut at ${kept.length} of ${text.length} characters (the read limit for this context window); ${next}` }] };
	});

	pi.on("tool_call", async (event) => {
		if (event.toolName === "read" && readLines && (event.input as { limit?: unknown }).limit === undefined)
			(event.input as { limit?: number }).limit = readLines;
		if (!(event.toolName in GUARDED)) return undefined;
		const input = event.input as { path?: unknown };
		const [write, existing] = GUARDED[event.toolName];
		const reply = await call({ guard: typeof input.path === "string" ? input.path : "", write, existing });
		if (reply.error !== undefined || !reply.result) return { block: true, reason: reply.error ?? "path refused" };
		input.path = reply.result; // the checked absolute path: Pi's own path rewriting cannot turn it into another file
		return undefined;
	});
}
