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

	// orchestrator.py's slim_tool_results for Pi: once the conversation passes AO_SLIM_CHARS (estimated as the engine does:
	// its text, and 2,500 tokens an image), a tool result older than the last KEEP_TOOL_ROUNDS assistant messages and
	// longer than STUB_OVER characters becomes a one-line note in each request. Pi's session keeps the full result.
	const KEEP_TOOL_ROUNDS = 2, STUB_OVER = 600, slimChars = Number(process.env.AO_SLIM_CHARS) || 0;
	const size = (content: unknown): number =>
		typeof content === "string" ? content.length : Array.isArray(content)
			? content.reduce((n: number, c: { type?: string; text?: string; thinking?: string; arguments?: unknown }) =>
				n + (c.type === "image" ? 2500 * 3.5 : (c.text ?? c.thinking ?? "").length + (c.arguments ? JSON.stringify(c.arguments).length : 0)), 0)
			: 0;
	// AO_NO_IMAGES (why the model cannot see, from orchestrator.vision_problem): every image in the session (an earlier
	// message's, a screenshot) becomes a note, or the server refuses each request ("image input is not supported").
	const noImages = process.env.AO_NO_IMAGES ?? "";
	const unseen = (content: unknown) => Array.isArray(content) && content.some((c: { type?: string }) => c.type === "image");
	const textOnly = <M extends { content: unknown }>(m: M): M => !noImages || !unseen(m.content) ? m : {
		...m,
		content: (m.content as { type?: string }[]).map((c) =>
			c.type === "image" ? { type: "text" as const, text: `[an image is left out: ${noImages}]` } : c),
	};
	// A write or edit rejected before it ran (a long file's arguments cut off, so a field is missing) comes back as "Validation
	// failed" plus an echo of everything the model sent. Small models then reopen the unchanged file and call the work done, and the
	// echo (tens of thousands of characters) rides along in every later request. Pi skips its tool_result hook for these, so the
	// rewrite is made on the request: the echo goes, and the note says nothing was written and what to do.
	const notWritten = (m: { role?: string; toolName?: string; isError?: boolean; content?: unknown }) =>
		m.role === "toolResult" && m.isError === true && (m.toolName === "write" || m.toolName === "edit") && Array.isArray(m.content) &&
		/^Validation failed/.test((m.content as { type?: string; text?: string }[]).map((c) => (c.type === "text" ? c.text ?? "" : "")).join(""));
	const NOT_WRITTEN = "NOTHING WAS WRITTEN: the file is exactly as it was and the step is NOT done. The call was rejected because its " +
		"arguments were malformed (usually the content was too long and was cut off, so a required field such as path was missing). " +
		"Do not say the work is complete and do not just reopen the file. Retry in smaller pieces: write a short skeleton file first, " +
		"then add each section with its own edit, always sending path and content in a complete, well-formed call.";
	pi.on("context", async (event) => {
		const refused = new Set(event.messages.flatMap((m) => notWritten(m as Parameters<typeof notWritten>[0]) ? [(m as { toolCallId: string }).toolCallId] : []));
		// a call the engine cut off for passing the role's max_tool_call_chars stays in the session with its partial arguments
		const cutOff = (m: { role?: string; stopReason?: string; content?: unknown }) => m.role === "assistant" && m.stopReason === "aborted" &&
			Array.isArray(m.content) && (m.content as { type?: string }[]).some((c) => c.type === "toolCall");
		const changed = !!noImages || refused.size > 0 || event.messages.some((m) => cutOff(m as Parameters<typeof cutOff>[0]));
		if (event.messages.some((m) => cutOff(m as Parameters<typeof cutOff>[0])))
			event = { ...event, messages: event.messages.map((m) => !cutOff(m as Parameters<typeof cutOff>[0]) ? m : { ...m, content: (m as { content: { type?: string; arguments?: Record<string, unknown> }[] }).content.map((c) => c.type === "toolCall"
				? { ...c, arguments: Object.fromEntries(Object.entries(c.arguments ?? {}).map(([k, v]) => [k, typeof v === "string" && v.length > 300 ? "[left out: this call was cut off]" : v])) } : c) }) };
		if (refused.size > 0) // the note replaces the echo, and the rejected call's own copy of the file goes too
			event = { ...event, messages: event.messages.map((m) => {
				if (refused.has((m as { toolCallId?: string }).toolCallId ?? "")) return { ...m, content: [{ type: "text" as const, text: NOT_WRITTEN }] };
				if (m.role !== "assistant" || !m.content.some((c) => c.type === "toolCall" && refused.has(c.id))) return m;
				return { ...m, content: m.content.map((c) => c.type === "toolCall" && refused.has(c.id)
					? { ...c, arguments: Object.fromEntries(Object.entries(c.arguments ?? {}).map(([k, v]) => [k, typeof v === "string" && v.length > 300 ? "[left out: this call was rejected]" : v])) } : c) };
			}) };
		if (noImages && event.messages.some((m) => unseen((m as { content?: unknown }).content)))
			event = { ...event, messages: event.messages.map((m) => textOnly(m as typeof m & { content: unknown })) };
		const rounds = event.messages.flatMap((m, i) => (m.role === "assistant" ? [i] : []));
		if (rounds.length <= KEEP_TOOL_ROUNDS) return changed ? { messages: event.messages } : undefined;
		if (event.messages.reduce((n, m) => n + size((m as { content?: unknown }).content), 0) <= slimChars) return changed ? { messages: event.messages } : undefined;
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
