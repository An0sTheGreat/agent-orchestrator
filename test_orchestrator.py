import contextlib
import io
import json
import os
import shutil
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

import orchestrator

REAL_RUN_AGENT = orchestrator.run_agent


class OrchestratorTests(unittest.TestCase):
    def test_extracts_plain_and_fenced_json(self):
        expected = {"verdict": "PASS"}
        self.assertEqual(orchestrator.extract_json(json.dumps(expected)), expected)
        self.assertEqual(orchestrator.extract_json('```json\n{"verdict":"PASS"}\n```'), expected)

    def test_rejects_path_escape_and_workflow_file(self):
        with tempfile.TemporaryDirectory(dir=Path(__file__).parent) as folder:
            root = Path(folder)
            with self.assertRaises(orchestrator.WorkflowError):
                orchestrator.path_is_allowed("../secret.txt", ["*.txt"], root)
            with self.assertRaises(orchestrator.WorkflowError):
                orchestrator.path_is_allowed("STATE.md", ["*.md"], root)

    def test_replace_requires_one_exact_match(self):
        with tempfile.TemporaryDirectory(dir=Path(__file__).parent) as folder:
            root = Path(folder)
            (root / "app.js").write_text("same\nsame\n", encoding="utf-8")
            result = orchestrator.execute_tool(
                "replace_text", {"path": "app.js", "old": "same", "new": "new"}, root, ["app.js"], {}, True
            )
            self.assertIn("found 2", result)

    def test_passed_state_is_written_to_selected_project(self):
        with tempfile.TemporaryDirectory(dir=Path(__file__).parent) as folder:
            root = Path(folder)
            orchestrator.append_passed_state({"id": "T-1", "outcome": "Works"}, "Done", root)
            state = (root / "STATE.md").read_text(encoding="utf-8")
            self.assertIn("## Passed: T-1", state)
            self.assertIn("- Outcome: Works", state)

    def test_project_configuration_updates_only_project_fields(self):
        original = {"roles": {"planner": {"base_url": "local"}}, "validation_commands": {}}
        updated = orchestrator.apply_project_configuration(
            original, '{"validation_commands":{"tests":["python","-m","unittest"]}}'
        )
        self.assertEqual(updated["roles"], original["roles"])
        self.assertEqual(updated["validation_commands"]["tests"], ["python", "-m", "unittest"])
        self.assertTrue(updated["project_configured"])

    def test_project_configuration_rejects_shell_commands(self):
        with self.assertRaises(orchestrator.WorkflowError):
            orchestrator.apply_project_configuration(
                {}, '{"validation_commands":{"unsafe":["powershell","-Command","Remove-Item","x"]}}'
            )

    def test_malformed_json_gets_a_bounded_repair(self):
        with patch.object(orchestrator, "ask_once", return_value='{"status":"COMPLETE"}') as repair:
            parsed, corrected = orchestrator.extract_json_with_repair({}, "planner", '{"status":', False)
        self.assertEqual(parsed, {"status": "COMPLETE"})
        self.assertEqual(corrected, '{"status":"COMPLETE"}')
        repair.assert_called_once()

    def test_invalid_planner_scope_gets_corrected(self):
        invalid = {
            "status": "TASK",
            "task": {
                "id": "T-1", "outcome": "shell", "allowed_files": ["a", "b", "c", "d"],
                "required_work": ["work"], "validation": ["check"], "stop_condition": "stop",
            },
        }
        valid = {
            "status": "TASK",
            "task": {
                "id": "T-1", "outcome": "shell", "allowed_files": ["a", "b", "c"],
                "required_work": ["work"], "validation": ["check"], "stop_condition": "stop",
            },
        }
        with tempfile.TemporaryDirectory(dir=Path(__file__).parent) as folder:
            with patch.object(orchestrator, "ask_once", side_effect=[json.dumps(invalid), json.dumps(valid)]) as planner:
                plan = orchestrator.request_plan({}, "system", "user", Path(folder), False)
        self.assertEqual(len(plan["task"]["allowed_files"]), 3)
        self.assertEqual(planner.call_count, 2)

    def test_invalid_reviewer_verdict_gets_corrected(self):
        with patch.object(orchestrator, "ask_once", return_value='{"verdict":"PASS","evidence":["ok"]}'):
            review, _ = orchestrator.extract_review_with_repair(
                {}, "system", "user", '{"verdict":"MAYBE"}', False
            )
        self.assertEqual(review["verdict"], "PASS")

    def test_think_splitter_handles_tags_split_across_chunks(self):
        splitter = orchestrator.ThinkSplitter()
        pieces = []
        for chunk in ["<thi", "nk>plan", " it</th", "ink>answer"]:
            pieces += splitter.feed(chunk)
        pieces += splitter.flush()
        text = {kind: "".join(t for k, t in pieces if k == kind) for kind in ("thinking", "text")}
        self.assertEqual(text, {"thinking": "plan it", "text": "answer"})

    def test_stream_chat_assembles_message_and_emits_deltas(self):
        class Response:
            headers = type("H", (), {"get_content_type": staticmethod(lambda: "text/event-stream")})()
            def __enter__(self): return self
            def __exit__(self, *args): return False
            def __iter__(self):
                chunks = [
                    {"choices": [{"delta": {"reasoning_content": "hm"}}]},
                    {"choices": [{"delta": {"content": "hi"}}]},
                    {"choices": [{"delta": {"tool_calls": [{"index": 0, "id": "c1", "function": {"name": "read_file", "arguments": "{\"path\""}}]}}]},
                    {"choices": [{"delta": {"tool_calls": [{"index": 0, "function": {"arguments": ": \"a\"}"}}]}}]},
                    {"choices": [], "usage": {"completion_tokens": 3}},
                ]
                for chunk in chunks:
                    yield f"data: {json.dumps(chunk)}\n".encode()
                yield b"data: [DONE]\n"

        client = orchestrator.ApiClient({"base_url": "http://x/v1", "api_key": "k"})
        client.role = "planner"
        with patch.object(orchestrator, "urlopen", return_value=Response()), patch.object(orchestrator, "emit") as emit:
            payload = client._stream_chat({"model": "m", "messages": []})
        message = payload["choices"][0]["message"]
        self.assertEqual((message["content"], message["reasoning_content"]), ("hi", "hm"))
        self.assertEqual(message["tool_calls"][0]["function"], {"name": "read_file", "arguments": "{\"path\": \"a\"}"})
        self.assertEqual(payload["usage"], {"completion_tokens": 3})
        self.assertEqual([c.kwargs["kind"] for c in emit.call_args_list], ["thinking", "text", "tool", "tool"])

    def _managed_client(self, status, **auto):
        auto = {"model_path": "u/M", "max_seq_length": 32768, "reasoning_budget": 0, **auto}
        client = orchestrator.ApiClient({"base_url": "http://x/v1", "management_url": "http://m", "api_key": "k",
                                         "expected_model_contains": "m", "auto_load": auto})
        calls = []

        def fake(base, method, route, body=None):
            calls.append((method, route, body))
            if route == "/api/inference/status":
                if status is None:
                    raise orchestrator.WorkflowError("down")
                return status
            return {}

        client._request_at = fake
        client.model_id = lambda: "u/M"
        return client, calls

    def _ensure(self, client):
        with patch.object(orchestrator, "LOADED_BY_RUN", {}) as mine, patch.object(orchestrator, "emit"):
            client.ensure_model("implementer", False)
        return mine

    def test_reloads_when_loaded_settings_differ(self):
        client, calls = self._managed_client({"requested_context_length": 32768, "requested_reasoning_budget": 8192})
        mine = self._ensure(client)
        loads = [c for c in calls if c[1] == "/api/inference/load"]
        self.assertEqual(loads[0][2], client.auto_load)
        self.assertIn("u/M", mine)

    def test_no_reload_when_settings_match_or_status_unreadable(self):
        for status in ({"requested_context_length": 32768, "requested_reasoning_budget": 0}, None, {}):
            client, calls = self._managed_client(status)
            self._ensure(client)
            self.assertFalse([c for c in calls if c[1] == "/api/inference/load"], status)

    def _failed_reload(self, models_after):
        client, calls = self._managed_client({"requested_context_length": 32768, "requested_reasoning_budget": 8192})
        seq = iter(["u/M"] + models_after)

        def model_id():
            try:
                value = next(seq)
            except StopIteration:
                value = ""
            if not value:
                raise orchestrator.WorkflowError("none loaded")
            return value

        client.model_id = model_id
        inner = client._request_at

        def fake(base, method, route, body=None):
            if route == "/api/inference/load":
                raise orchestrator.WorkflowError("OOM")
            return inner(base, method, route, body)

        client._request_at = fake
        return client

    def _ensure_ret(self, client):
        with patch.object(orchestrator, "LOADED_BY_RUN", {}), patch.object(orchestrator, "emit"):
            return client.ensure_model("implementer", False)

    def test_failed_reload_continues_only_if_model_still_loaded(self):
        self.assertEqual(self._ensure_ret(self._failed_reload(["u/M"])), "u/M")
        for after in ([""], ["other"]):
            with self.assertRaisesRegex(orchestrator.WorkflowError, "OOM"):
                self._ensure_ret(self._failed_reload(after))

    def test_thinking_fallback_is_sticky(self):
        client = orchestrator.ApiClient({"base_url": "http://x/v1", "api_key": "k"})
        client._stream_chat, bodies = self._fake_stream([self._reply("length", reasoning="a"), self._reply("stop", content="ok"), self._reply("stop", content="ok")])
        with patch.object(orchestrator, "EVENTS", True), patch.object(orchestrator, "emit"), patch.object(client, "_monitor_row", return_value={}):
            client.complete("m", [], {})
            client.complete("m", [], {})
        self.assertNotIn("enable_thinking", bodies[0])
        self.assertIs(bodies[2]["enable_thinking"], False)

    def test_discarded_attempt_is_closed_before_retry(self):
        client = orchestrator.ApiClient({"base_url": "http://x/v1", "api_key": "k"})
        client.role = "planner"
        order = []
        replies = [self._reply("length", reasoning="a"), self._reply("stop", content="ok")]

        def stream(body):
            order.append("delta")
            return replies[order.count("delta") - 1]

        client._stream_chat = stream
        with patch.object(orchestrator, "EVENTS", True), patch.object(client, "_monitor_row", return_value={}), \
                patch.object(orchestrator, "emit", side_effect=lambda e, **k: order.append((e, k.get("final")))):
            client.complete("m", [], {})
        self.assertEqual(order[:3], ["delta", ("done", False), "delta"])

    def _fake_stream(self, replies):
        bodies = []

        def stream(body):
            bodies.append(body)
            return replies[len(bodies) - 1]

        return stream, bodies

    def _reply(self, finish, content="", reasoning="", calls=None):
        message = {"role": "assistant", "content": content}
        if reasoning:
            message["reasoning_content"] = reasoning
        if calls:
            message["tool_calls"] = calls
        return {"choices": [{"message": message, "finish_reason": finish}], "usage": {}, "_streamed": True}

    def test_cut_off_while_thinking_retries_without_thinking(self):
        call = [{"id": "c", "type": "function", "function": {"name": "write_file", "arguments": "{}"}}]
        client = orchestrator.ApiClient({"base_url": "http://x/v1", "api_key": "k"})
        client._stream_chat, bodies = self._fake_stream([self._reply("length", reasoning="hmm"), self._reply("tool_calls", calls=call)])
        with patch.object(orchestrator, "EVENTS", True), patch.object(orchestrator, "emit"), patch.object(client, "_monitor_row", return_value={}):
            message = client.complete("m", [], {})
        self.assertNotIn("enable_thinking", bodies[0])
        self.assertIs(bodies[1]["enable_thinking"], False)
        self.assertEqual(message["tool_calls"], call)

    def test_still_empty_after_retry_raises_output_limit_error(self):
        client = orchestrator.ApiClient({"base_url": "http://x/v1", "api_key": "k"})
        client._stream_chat, bodies = self._fake_stream([self._reply("length", reasoning="a")] * 2)
        with patch.object(orchestrator, "EVENTS", True), patch.object(orchestrator, "emit"):
            with self.assertRaisesRegex(orchestrator.WorkflowError, "output limit"):
                client.complete("m", [], {})
        self.assertEqual(len(bodies), 2)

    def test_truncated_but_non_empty_reply_is_not_retried(self):
        client = orchestrator.ApiClient({"base_url": "http://x/v1", "api_key": "k"})
        client._stream_chat, bodies = self._fake_stream([self._reply("length", content="partial answer")])
        with patch.object(orchestrator, "EVENTS", True), patch.object(orchestrator, "emit"), patch.object(client, "_monitor_row", return_value={}):
            self.assertEqual(client.complete("m", [], {})["content"], "partial answer")
        self.assertEqual(len(bodies), 1)

    def test_stream_chat_reports_finish_reason(self):
        class Response:
            headers = type("H", (), {"get_content_type": staticmethod(lambda: "text/event-stream")})()
            def __enter__(self): return self
            def __exit__(self, *args): return False
            def __iter__(self):
                yield b'data: {"choices": [{"delta": {"reasoning_content": "x"}}]}\n'
                yield b'data: {"choices": [{"delta": {}, "finish_reason": "length"}]}\n'
                yield b"data: [DONE]\n"

        client = orchestrator.ApiClient({"base_url": "http://x/v1", "api_key": "k"})
        with patch.object(orchestrator, "urlopen", return_value=Response()), patch.object(orchestrator, "emit"):
            self.assertEqual(client._stream_chat({})["choices"][0]["finish_reason"], "length")

    def test_enable_thinking_is_sent_top_level_only_when_configured(self):
        for sampling, expected in (({"enable_thinking": False, "max_tokens": 5}, False), ({"max_tokens": 5}, "absent")):
            config = {"roles": {"planner": {"base_url": "http://x/v1", "api_key": "k", "sampling": sampling}}}
            client, extra = orchestrator.role_settings(config, "planner")
            sent = []
            client._request = lambda method, route, body=None: sent.append(body) or {"choices": [{"message": {"content": "ok"}, "finish_reason": "stop"}]}
            with patch.object(orchestrator, "EVENTS", False):
                client.complete("m", [], extra)
            self.assertEqual(sent[0].get("enable_thinking", "absent"), expected)

    def test_unload_only_models_loaded_by_this_run(self):
        client = orchestrator.ApiClient({"base_url": "http://x/v1", "management_url": "http://x", "api_key": "k"})
        client.role = "reviewer"
        mine = {"mine.gguf": client}
        with patch.object(orchestrator, "LOADED_BY_RUN", mine),                 patch.object(orchestrator.ApiClient, "_request_at", return_value={}) as request:
            orchestrator.unload_models()
        self.assertEqual([c.args[3] for c in request.call_args_list], [{"model_path": "mine.gguf"}])

    def test_implementer_sees_current_files_and_missing_ones(self):
        text = orchestrator.allowed_files_context({"a.js": "let x = 1;", "b.js": None})
        self.assertIn("--- a.js ---\nlet x = 1;", text)
        self.assertIn("b.js --- (does not exist yet; create it with write_file)", text)

    def test_reading_a_missing_allowed_file_says_to_create_it(self):
        with tempfile.TemporaryDirectory(dir=Path(__file__).parent) as folder:
            result = orchestrator.execute_tool("read_file", {"path": "new.js"}, Path(folder), ["new.js"], {}, True)
        self.assertIn("create it with write_file", result)

    def test_text_tool_calls_recovered_from_json_blocks(self):
        text = 'x {"name": "write_file", "arguments": {"path": "a.txt", "content": "hi"}} y {"name": "nope", "arguments": {}}'
        calls = orchestrator.text_tool_calls(text, {"write_file"})
        self.assertEqual(len(calls), 1)
        self.assertEqual(json.loads(calls[0]["function"]["arguments"])["path"], "a.txt")

    def test_run_agent_stops_when_text_tool_calls_repeat(self):
        content = '```json\n{"name": "write_file", "arguments": {"path": "a.txt", "content": "hi"}}\n```\nPASSED'

        class Client:
            completions = 0

            def ensure_model(self, role, interactive):
                return "m"

            def complete(self, model, messages, sampling, tools):
                self.completions += 1
                return {"role": "assistant", "content": content}

        client = Client()
        with tempfile.TemporaryDirectory(dir=Path(__file__).parent) as folder:
            with patch.object(orchestrator, "role_settings", return_value=(client, {})), patch.object(orchestrator, "emit"):
                result = orchestrator.run_agent({"max_tool_steps": 5}, "implementer", "s", "u", Path(folder), ["a.txt"], True, False)
            self.assertEqual((Path(folder) / "a.txt").read_text(encoding="utf-8").strip(), "hi")
        self.assertEqual(result, content)
        self.assertEqual(client.completions, 2)

    def test_run_agent_native_turn_breaks_text_repeat_chain(self):
        text = '```json\n{"name": "read_file", "arguments": {"path": "a.txt"}}\n```'
        native = {"role": "assistant", "content": "", "tool_calls": [{"id": "c1", "function": {"name": "read_file", "arguments": json.dumps({"path": "a.txt"})}}]}
        replies = [{"role": "assistant", "content": text}, native, {"role": "assistant", "content": text}, {"role": "assistant", "content": "done"}]

        class Client:
            completions = 0

            def ensure_model(self, role, interactive):
                return "m"

            def complete(self, model, messages, sampling, tools):
                self.completions += 1
                return replies.pop(0)

        client = Client()
        with tempfile.TemporaryDirectory(dir=Path(__file__).parent) as folder:
            (Path(folder) / "a.txt").write_text("hi", encoding="utf-8")
            with patch.object(orchestrator, "role_settings", return_value=(client, {})), patch.object(orchestrator, "emit"):
                result = orchestrator.run_agent({"max_tool_steps": 6}, "reviewer", "s", "u", Path(folder), ["a.txt"], False, False)
        self.assertEqual(result, "done")
        self.assertEqual(client.completions, 4)

    def test_run_agent_native_tool_calls_still_execute(self):
        native = {"role": "assistant", "content": "", "tool_calls": [{"id": "c1", "function": {"name": "write_file", "arguments": json.dumps({"path": "a.txt", "content": "hi"})}}]}
        replies = [native, dict(native, tool_calls=[dict(native["tool_calls"][0], id="c2")]), {"role": "assistant", "content": "done"}]

        class Client:
            def ensure_model(self, role, interactive):
                return "m"

            def complete(self, model, messages, sampling, tools):
                return replies.pop(0)

        with tempfile.TemporaryDirectory(dir=Path(__file__).parent) as folder:
            with patch.object(orchestrator, "role_settings", return_value=(Client(), {})), patch.object(orchestrator, "emit"):
                result = orchestrator.run_agent({"max_tool_steps": 5}, "implementer", "s", "u", Path(folder), ["a.txt"], True, False)
        self.assertEqual((result, replies), ("done", []))

    def test_run_agent_retries_without_tools_when_template_lacks_tool_support(self):
        content = '{"verdict":"PASS","evidence":["x"]}'
        error = (
            'API 400: {"error":{"message":"Client-supplied tools or tool-call history require a GGUF chat template with tool-call support; '
            'the current model/template does not advertise tools.","type":"invalid_request_error","param":"tools","code":"unsupported_parameter"}}'
        )

        class Client:
            def __init__(self):
                self.tools_seen = []

            def ensure_model(self, role, interactive):
                return "m"

            def complete(self, model, messages, sampling, tools=None):
                self.tools_seen.append(tools)
                if tools:
                    raise orchestrator.WorkflowError(error)
                return {"role": "assistant", "content": content}

        client = Client()
        with tempfile.TemporaryDirectory(dir=Path(__file__).parent) as folder:
            with patch.object(orchestrator, "role_settings", return_value=(client, {})), patch.object(orchestrator, "emit"):
                result = orchestrator.run_agent({"max_tool_steps": 5}, "reviewer", "s", "u", Path(folder), ["a.txt"], False, False)
        self.assertEqual(result, content)
        self.assertEqual(len(client.tools_seen), 2)
        self.assertTrue(client.tools_seen[0])
        self.assertFalse(client.tools_seen[1])

    def test_run_agent_propagates_other_api_errors(self):
        class Client:
            completions = 0

            def ensure_model(self, role, interactive):
                return "m"

            def complete(self, model, messages, sampling, tools=None):
                self.completions += 1
                raise orchestrator.WorkflowError("API 500: boom")

        client = Client()
        with tempfile.TemporaryDirectory(dir=Path(__file__).parent) as folder:
            with patch.object(orchestrator, "role_settings", return_value=(client, {})), patch.object(orchestrator, "emit"):
                with self.assertRaises(orchestrator.WorkflowError) as ctx:
                    orchestrator.run_agent({"max_tool_steps": 5}, "reviewer", "s", "u", Path(folder), ["a.txt"], False, False)
        self.assertIn("API 500", str(ctx.exception))
        self.assertEqual(client.completions, 1)

    def test_model_id_skips_unloaded_entries(self):
        client = orchestrator.ApiClient({"base_url": "http://x/v1", "api_key": "k"})
        with patch.object(client, "_request", return_value={"data": [{"id": "a", "loaded": False}, {"id": "b", "loaded": True}]}):
            self.assertEqual(client.model_id(), "b")

    def test_model_id_raises_when_nothing_loaded(self):
        client = orchestrator.ApiClient({"base_url": "http://x/v1", "api_key": "k"})
        with patch.object(client, "_request", return_value={"data": [{"id": "a", "loaded": False}]}):
            with self.assertRaises(orchestrator.WorkflowError):
                client.model_id()

    def test_model_id_accepts_entries_without_loaded_flag(self):
        client = orchestrator.ApiClient({"base_url": "http://x/v1", "api_key": "k"})
        with patch.object(client, "_request", return_value={"data": [{"id": "x"}]}):
            self.assertEqual(client.model_id(), "x")

    def test_run_agent_saves_last_reply_when_no_tool_runs(self):
        class Client:
            def ensure_model(self, role, interactive):
                return "m"

            def complete(self, model, messages, sampling, tools=None):
                return {"role": "assistant", "content": "BLOCKED: nothing to do"}

        with tempfile.TemporaryDirectory(dir=Path(__file__).parent) as folder:
            with patch.object(orchestrator, "role_settings", return_value=(Client(), {})), patch.object(orchestrator, "emit"):
                with self.assertRaises(orchestrator.WorkflowError) as ctx:
                    orchestrator.run_agent({"max_tool_steps": 5}, "implementer", "s", "u", Path(folder), ["a.txt"], True, False)
            self.assertIn("BLOCKED: nothing to do", str(ctx.exception))
            self.assertIn("BLOCKED: nothing to do", (Path(folder) / "RESULT.md").read_text(encoding="utf-8"))


    def _scripted_agent(self, replies, folder, allowed, config=None):
        """Run the writable implementer against scripted replies; returns (result, client)."""
        class Client:
            completions = 0
            messages = None

            def ensure_model(self, role, interactive):
                return "m"

            def complete(self, model, messages, sampling, tools=None):
                self.completions += 1
                self.messages = [dict(m) for m in messages]
                return replies[min(self.completions - 1, len(replies) - 1)]

        client = Client()
        with patch.object(orchestrator, "role_settings", return_value=(client, {})), patch.object(orchestrator, "emit"):
            result = REAL_RUN_AGENT(config or {"max_tool_steps": 12}, "implementer", "s", "u", Path(folder), allowed, True, False)
        return result, client

    @staticmethod
    def _native(name, **arguments):
        return {"role": "assistant", "content": "", "tool_calls": [{"id": "c", "function": {"name": name, "arguments": json.dumps(arguments)}}]}

    def test_syntax_gate_js_py_json(self):
        with tempfile.TemporaryDirectory(dir=Path(__file__).parent) as folder:
            root = Path(folder)
            (root / "ok.py").write_text("x = 1\n", encoding="utf-8")
            (root / "bad.py").write_text("def f(:\n", encoding="utf-8")
            (root / "ok.json").write_text('{"a": 1}', encoding="utf-8")
            (root / "bad.json").write_text('{"a": }', encoding="utf-8")
            defects, line = orchestrator.syntax_gate(root, ["ok.py", "bad.py", "ok.json", "bad.json", "missing.js"])
            self.assertEqual(sorted(d["file"] for d in defects), ["bad.json", "bad.py"])
            self.assertIn("FAILED", line)
            defects, line = orchestrator.syntax_gate(root, ["ok.py", "ok.json"])
            self.assertEqual(defects, [])
            self.assertEqual(line, "SYNTAX CHECK: passed (ok.py, ok.json)")

    @unittest.skipUnless(shutil.which("node"), "node not installed")
    def test_syntax_gate_js(self):
        with tempfile.TemporaryDirectory(dir=Path(__file__).parent) as folder:
            root = Path(folder)
            (root / "bad.js").write_text("const a = 1; const a = 2;\n", encoding="utf-8")
            (root / "ok.js").write_text("const a = 1;\n", encoding="utf-8")
            defects, _ = orchestrator.syntax_gate(root, ["bad.js", "ok.js"])
            self.assertEqual([d["file"] for d in defects], ["bad.js"])
            self.assertEqual(orchestrator.syntax_gate(root, ["ok.js"])[0], [])

    def test_syntax_errors_go_back_to_implementer_then_skip_reviewer(self):
        broken = self._native("write_file", path="a.json", content="{bad")
        done = {"role": "assistant", "content": "T PASSED"}
        with tempfile.TemporaryDirectory(dir=Path(__file__).parent) as folder:
            root = self.make_project(folder)
            task = dict(self.TASK, allowed_files=["a.json"])
            (root / "TASK.md").write_text("task\n", encoding="utf-8")
            roles = []

            def agent(config, role, *rest):
                roles.append(role)
                if role == "reviewer":
                    return '{"verdict":"PASS"}'
                result, client = self._scripted_agent([broken, done], folder, ["a.json"])
                seen.append(client.messages[-1]["content"])
                return result

            seen = []
            with patch.object(orchestrator, "run_agent", side_effect=agent), patch.object(orchestrator, "emit"):
                with self.assertRaises(orchestrator.WorkflowError):
                    orchestrator.run_review_loop({}, {"implementer": "", "reviewer": ""}, task, {}, 0, "", "implementing", 1, root, False)
            self.assertNotIn("reviewer", roles)
            self.assertTrue(seen)
            # Round 1 starts from a clean file, so its final send-back must carry the syntax error; later rounds start
            # from the already-broken file (unchanged in their own turn) and are failed by the round gate instead.
            self.assertIn("SYNTAX CHECK FAILED", seen[0])
            self.assertIn("a.json", seen[0])
            review = json.loads((root / "REVIEW.md").read_text(encoding="utf-8"))
            self.assertEqual(review["verdict"], "FAIL")
            self.assertEqual(review["defects"][0]["file"], "a.json")

    def test_syntax_errors_reach_implementer_in_same_conversation(self):
        broken = self._native("write_file", path="a.json", content="{bad")
        fixed = self._native("write_file", path="a.json", content="{}")
        done = {"role": "assistant", "content": "PASSED"}
        with tempfile.TemporaryDirectory(dir=Path(__file__).parent) as folder:
            result, client = self._scripted_agent([broken, done, fixed, done], folder, ["a.json"])
            self.assertEqual(result, "PASSED")
            self.assertTrue(any("SYNTAX CHECK FAILED" in str(m.get("content")) for m in client.messages))
            self.assertEqual(client.completions, 4)

    def test_unexecuted_commented_tool_call_is_not_accepted(self):
        write = self._native("write_file", path="a.txt", content="x")
        bad = {"role": "assistant", "content": '{"name": "replace_text", "arguments": {"path": "a.txt", "old": "x", // comment\n "new": "y"}}\nPASSED'}
        with tempfile.TemporaryDirectory(dir=Path(__file__).parent) as folder:
            result, client = self._scripted_agent([write, bad], folder, ["a.txt"])
        self.assertTrue(result.startswith("UNVERIFIED:"))
        self.assertIn("NOT executed", result)
        self.assertEqual(client.completions, 5)  # write + reply + 3 send-backs

    def test_passed_after_failed_tool_call_is_not_accepted(self):
        write = self._native("write_file", path="a.txt", content="x")
        fail = self._native("replace_text", path="a.txt", old="zzz", new="y")
        done = {"role": "assistant", "content": "PASSED"}
        with tempfile.TemporaryDirectory(dir=Path(__file__).parent) as folder:
            result, _ = self._scripted_agent([write, fail, done], folder, ["a.txt"])
            self.assertTrue(result.startswith("UNVERIFIED:"))
            ok = self._native("replace_text", path="a.txt", old="x", new="y")
            result, _ = self._scripted_agent([write, fail, done, ok, done], folder, ["a.txt"])
            self.assertEqual(result, "PASSED")

    @unittest.skipUnless(shutil.which("node"), "node not installed")
    def test_syntax_gate_js_non_ascii_error(self):
        with tempfile.TemporaryDirectory(dir=Path(__file__).parent) as folder:
            root = Path(folder)
            (root / "u.js").write_text('const s = "\u0141\u00f3d\u017a \u00c1pp"; const s = 1;\n', encoding="utf-8")
            defects, _ = orchestrator.syntax_gate(root, ["u.js"])
            self.assertEqual([d["file"] for d in defects], ["u.js"])
            # Node's own message must survive decoding, not just the exit-code fallback.
            self.assertIn("already been declared", defects[0]["evidence"])

    def test_syntax_gate_bom_and_unchanged_files(self):
        with tempfile.TemporaryDirectory(dir=Path(__file__).parent) as folder:
            root = Path(folder)
            (root / "b.json").write_text("\ufeff{}", encoding="utf-8")
            (root / "b.py").write_text("\ufeffx = 1\n", encoding="utf-8")
            self.assertEqual(orchestrator.syntax_gate(root, ["b.json", "b.py"])[0], [])
            (root / "tsconfig.json").write_text('{\n// c\n"a": 1}', encoding="utf-8")
            before = {"tsconfig.json": (root / "tsconfig.json").read_text(encoding="utf-8")}
            defects, line = orchestrator.syntax_gate(root, ["tsconfig.json"], before)
            self.assertEqual(defects, [])
            self.assertIn("pre-existing", line)
            self.assertIn("tsconfig.json", line)
            (root / "tsconfig.json").write_text('{\n// c\n"a": 2}', encoding="utf-8")
            self.assertEqual([d["file"] for d in orchestrator.syntax_gate(root, ["tsconfig.json"], before)[0]], ["tsconfig.json"])

    def test_round_with_unchanged_broken_file_reaches_reviewer_with_syntax_line(self):
        with tempfile.TemporaryDirectory(dir=Path(__file__).parent) as folder:
            root = self.make_project(folder)
            task = dict(self.TASK, allowed_files=["tsconfig.json", "ok.json"])
            (root / "TASK.md").write_text("task\n", encoding="utf-8")
            (root / "tsconfig.json").write_text('{\n// c\n"a": 1}', encoding="utf-8")
            (root / "ok.json").write_text("{}", encoding="utf-8")
            before = orchestrator.snapshot(root, task["allowed_files"])
            inputs = []

            def agent(config, role, system, user, *rest):
                inputs.append((role, user))
                return '{"verdict":"PASS"}' if role == "reviewer" else "PASSED"

            with patch.object(orchestrator, "run_agent", side_effect=agent), patch.object(orchestrator, "emit"):
                orchestrator.run_review_loop({}, {"implementer": "", "reviewer": ""}, task, before, 0, "", "implementing", 1, root, False)
            reviewer = [user for role, user in inputs if role == "reviewer"]
            self.assertEqual(len(reviewer), 1)
            self.assertIn("SYNTAX CHECK:", reviewer[0])
            self.assertIn("pre-existing", reviewer[0])

    def test_flagged_answer_on_last_step_returns_unverified(self):
        write = self._native("write_file", path="a.txt", content="x")
        fail = self._native("replace_text", path="a.txt", old="zzz", new="y")
        done = {"role": "assistant", "content": "PASSED"}
        with tempfile.TemporaryDirectory(dir=Path(__file__).parent) as folder:
            result, _ = self._scripted_agent([write, fail, done], folder, ["a.txt"], {"max_tool_steps": 3})
        self.assertTrue(result.startswith("UNVERIFIED:"))

    def test_harmless_failed_call_is_not_unverified(self):
        write = self._native("write_file", path="a.txt", content="x")
        harmless = {"role": "assistant", "content": "", "tool_calls": [{"id": "c", "function": {"name": "run_validation", "arguments": json.dumps({"name": "nope"})}}]}
        done = {"role": "assistant", "content": "PASSED"}
        with tempfile.TemporaryDirectory(dir=Path(__file__).parent) as folder:
            result, _ = self._scripted_agent([write, harmless, done], folder, ["a.txt"])
        self.assertEqual(result, "PASSED")

    def _task(self, files=1, work=1):
        return {"id": "T", "outcome": "o", "allowed_files": [f"f{i}.txt" for i in range(files)],
                "required_work": ["w"] * work, "validation": ["v"], "stop_condition": "s"}

    def test_validate_task_default_limits(self):
        with tempfile.TemporaryDirectory(dir=Path(__file__).parent) as folder:
            root = Path(folder)
            orchestrator.validate_task(self._task(3, 7), root)
            with self.assertRaises(orchestrator.WorkflowError):
                orchestrator.validate_task(self._task(4, 1), root)
            with self.assertRaises(orchestrator.WorkflowError):
                orchestrator.validate_task(self._task(1, 8), root)

    def test_validate_task_custom_limits(self):
        limits = orchestrator.task_limits({"task_limits": {"max_allowed_files": 1, "max_required_work": 3}})
        self.assertEqual(limits, (1, 3))
        with tempfile.TemporaryDirectory(dir=Path(__file__).parent) as folder:
            root = Path(folder)
            orchestrator.validate_task(self._task(1, 3), root, limits)
            with self.assertRaises(orchestrator.WorkflowError) as ctx:
                orchestrator.validate_task(self._task(2, 1), root, limits)
            self.assertIn("1-1 files", str(ctx.exception))
            with self.assertRaises(orchestrator.WorkflowError) as ctx:
                orchestrator.validate_task(self._task(1, 4), root, limits)
            self.assertIn("1-3 required-work", str(ctx.exception))

    def test_task_limits_defaults_and_invalid(self):
        self.assertEqual(orchestrator.task_limits({}), (3, 7))
        self.assertEqual(orchestrator.task_limits({"task_limits": {"max_required_work": 5}}), (3, 5))
        for bad in ({"max_allowed_files": 0}, {"max_required_work": "7"}, {"max_allowed_files": True}, {"max_required_work": 1.5}, []):
            with self.assertRaises(orchestrator.WorkflowError):
                orchestrator.task_limits({"task_limits": bad})

    def test_team_context(self):
        config = {
            "task_limits": {"max_allowed_files": 2, "max_required_work": 4},
            "roles": {
                "implementer": {
                    "display_name": "Small-Coder",
                    "auto_load": {"gguf_variant": "Q4_K_M", "max_seq_length": 32768},
                    "sampling": {"max_tokens": 4096},
                },
                "reviewer": {"expected_model_contains": "rev-model"},
            },
        }
        text = orchestrator.team_context(config)
        self.assertIn("Implementer: Small-Coder, quant Q4_K_M, context 32768 tokens, max output 4096 tokens", text)
        self.assertIn("Reviewer: rev-model", text)
        self.assertNotIn("None", text)
        self.assertNotIn("context", text.split("Reviewer:")[1])
        self.assertIn("Task limits: at most 2 allowed files and 4 required-work items per task", text)

    TASK = {
        "id": "T-9", "outcome": "Make it work", "allowed_files": ["app.js"],
        "required_work": ["work"], "validation": ["check"], "stop_condition": "stop",
    }

    def make_project(self, folder, files=orchestrator.WORKFLOW_FILES):
        root = Path(folder)
        for name in files:
            (root / name).write_text("{}" if name == "config.json" else "text\n", encoding="utf-8")
        if "PROJECT.md" in files:
            (root / "PROJECT.md").write_text("Build a real thing\n", encoding="utf-8")
        return root

    def run_main(self, root, plan=None, agent=None, reviews=("PASS",), once=True, on_plan=None):
        verdicts = iter(reviews)
        planner = patch.object(
            orchestrator, "request_plan",
            **({"side_effect": on_plan} if on_plan else {"return_value": plan or {"status": "TASK", "task": self.TASK}}),
        )
        agent_stub = patch.object(orchestrator, "run_agent", side_effect=agent or (lambda *a: "did it"))
        review = patch.object(
            orchestrator, "extract_review_with_repair",
            side_effect=lambda *a: ({"verdict": next(verdicts)}, "feedback text"),
        )
        argv = patch.object(orchestrator.sys, "argv", ["o", "--project", str(root), *(["--once"] if once else []), "--non-interactive"])
        configure = patch.object(orchestrator, "configure_project", side_effect=lambda config, *a: config)
        with planner as plan_mock, agent_stub as agent_mock, review, argv, configure:
            code = orchestrator._run()
        return code, plan_mock, agent_mock

    def test_checkpoint_tracks_phase_and_round_and_is_removed_on_pass(self):
        with tempfile.TemporaryDirectory(dir=Path(__file__).parent) as folder:
            root = self.make_project(folder)
            seen = []

            def agent(config, role, *rest):
                data = json.loads((root / ".ao_checkpoint.json").read_text(encoding="utf-8"))
                seen.append((role, data["phase"], data["round"], data["reviewer_feedback"]))
                return "did it"

            code, _, _ = self.run_main(root, agent=agent, reviews=("FAIL", "PASS"))
            self.assertEqual(code, 0)
            self.assertEqual(
                seen,
                [("implementer", "implementing", 0, "None; this is the first attempt."),
                 ("reviewer", "reviewing", 0, "None; this is the first attempt."),
                 ("implementer", "implementing", 1, "feedback text"),
                 ("reviewer", "reviewing", 1, "feedback text")],
            )
            self.assertFalse((root / ".ao_checkpoint.json").exists())
            self.assertFalse((root / ".ao_checkpoint.json.tmp").exists())
            handoff = (root / "HANDOFF.md").read_text(encoding="utf-8")
            self.assertIn("No task in progress.", handoff)
            self.assertIn("T-9", handoff)

    def test_checkpoint_removed_when_blocked(self):
        with tempfile.TemporaryDirectory(dir=Path(__file__).parent) as folder:
            root = self.make_project(folder)
            (root / "config.json").write_text('{"max_review_rounds": 0}', encoding="utf-8")
            with self.assertRaises(orchestrator.WorkflowError):
                self.run_main(root, reviews=("FAIL",))
            self.assertFalse((root / ".ao_checkpoint.json").exists())
            self.assertIn("T-9 blocked", (root / "ISSUES.md").read_text(encoding="utf-8"))

    def test_handoff_with_and_without_checkpoint(self):
        with tempfile.TemporaryDirectory(dir=Path(__file__).parent) as folder:
            root = self.make_project(folder)
            orchestrator.save_checkpoint(root, self.TASK, {"app.js": None}, 1, "x" * 3000, "reviewing", 3)
            text = (root / "HANDOFF.md").read_text(encoding="utf-8")
            for expected in ("T-9", "Make it work", "app.js", "Reviewer checking", "Review round 2 of 3",
                             "Delete .ao_checkpoint.json to discard it and plan a new task."):
                self.assertIn(expected, text)
            self.assertIn("x" * 2000, text)
            self.assertNotIn("x" * 2001, text)
            orchestrator.save_checkpoint(root, self.TASK, {}, 0, "", "implementing", 3)
            self.assertIn("Implementer working", (root / "HANDOFF.md").read_text(encoding="utf-8"))
            (root / "STATE.md").write_text("## Passed: T-1\n- x\n\n## Passed: T-2\n- y\n", encoding="utf-8")
            orchestrator.clear_checkpoint(root)
            text = (root / "HANDOFF.md").read_text(encoding="utf-8")
            self.assertIn("No task in progress.", text)
            self.assertIn("T-2", text)
            self.assertFalse((root / ".ao_checkpoint.json").exists())

    def test_resume_skips_planner_and_starts_at_stored_round(self):
        with tempfile.TemporaryDirectory(dir=Path(__file__).parent) as folder:
            root = self.make_project(folder)
            (root / "app.js").write_text("newer\n", encoding="utf-8")
            orchestrator.save_checkpoint(root, self.TASK, {"app.js": "old\n"}, 1, "fix the bug", "implementing", 3)
            inputs = []

            def agent(config, role, system, user, *rest):
                inputs.append((role, user))
                return "did it"

            code, planner, _ = self.run_main(root, agent=agent)
            self.assertEqual(code, 0)
            planner.assert_not_called()
            self.assertEqual(inputs[0][0], "implementer")
            self.assertIn("fix the bug", inputs[0][1])
            reviewer_input = inputs[1][1]
            self.assertIn("Review round: 2", reviewer_input)
            self.assertIn("-old", reviewer_input)
            self.assertIn("+new", reviewer_input)
            self.assertIn("Make it work", (root / "TASK.md").read_text(encoding="utf-8"))
            self.assertFalse((root / ".ao_checkpoint.json").exists())

    def test_resume_in_reviewing_phase_reuses_result_unless_missing(self):
        with tempfile.TemporaryDirectory(dir=Path(__file__).parent) as folder:
            root = self.make_project(folder)
            orchestrator.save_checkpoint(root, self.TASK, {}, 0, "fb", "reviewing", 3)
            (root / "RESULT.md").write_text("earlier result\n", encoding="utf-8")
            _, _, agent = self.run_main(root)
            self.assertEqual([call.args[1] for call in agent.call_args_list], ["reviewer"])
            self.assertIn("earlier result", agent.call_args_list[0].args[3])
            orchestrator.save_checkpoint(root, self.TASK, {}, 0, "fb", "reviewing", 3)
            (root / "RESULT.md").unlink()
            (root / "TASK.md").write_text("task\n", encoding="utf-8")
            prompts = {"implementer": "", "reviewer": ""}
            with patch.object(orchestrator, "run_agent", return_value="did it") as agent, patch.object(
                orchestrator, "extract_review_with_repair", return_value=({"verdict": "PASS"}, "ok")
            ):
                orchestrator.run_review_loop({}, prompts, self.TASK, {}, 0, "fb", "reviewing", 2, root, False)
            self.assertEqual([call.args[1] for call in agent.call_args_list], ["implementer", "reviewer"])

    def test_corrupt_checkpoint_is_discarded_and_planning_proceeds(self):
        bad_task = dict(self.TASK, allowed_files=["a", "b", "c", "d"])
        cases = ["{not json", json.dumps({"version": 2}),
                 json.dumps({"version": 1, "task": bad_task, "before": {}, "round": 0,
                             "reviewer_feedback": "", "phase": "implementing"}),
                 json.dumps({"version": 1, "task": self.TASK, "before": {}, "round": 0, "phase": "implementing"})]
        for content in cases:
            with tempfile.TemporaryDirectory(dir=Path(__file__).parent) as folder:
                root = self.make_project(folder)
                (root / ".ao_checkpoint.json").write_text(content, encoding="utf-8")
                gone_at_plan = []

                def on_plan(*args):
                    gone_at_plan.append(not (root / ".ao_checkpoint.json").exists())
                    return {"status": "COMPLETE"}

                code, planner, _ = self.run_main(root, on_plan=on_plan)
                self.assertEqual(code, 0)
                planner.assert_called_once()
                self.assertEqual(gone_at_plan, [True])
                self.assertFalse((root / ".ao_checkpoint.json").exists())

    def _kill_between(self, root, reviews, rounds=None):
        """Run until the completion entry is written but clear_checkpoint 'kills' the process."""
        if rounds is not None:
            (root / "config.json").write_text(json.dumps({"max_review_rounds": rounds}), encoding="utf-8")
        with patch.object(orchestrator, "clear_checkpoint", side_effect=KeyboardInterrupt):
            with self.assertRaises(KeyboardInterrupt):
                self.run_main(root, reviews=reviews)
        self.assertTrue((root / ".ao_checkpoint.json").exists())

    def test_kill_between_pass_append_and_clear_resumes_without_duplicate(self):
        with tempfile.TemporaryDirectory(dir=Path(__file__).parent) as folder:
            root = self.make_project(folder)
            self._kill_between(root, ("PASS",))
            self.assertEqual((root / "STATE.md").read_text(encoding="utf-8").count("## Passed: T-9"), 1)
            code, planner, agent = self.run_main(root, reviews=("FAIL",))
            self.assertEqual(code, 0)
            planner.assert_not_called()
            agent.assert_not_called()
            self.assertEqual((root / "STATE.md").read_text(encoding="utf-8").count("## Passed: T-9"), 1)
            self.assertFalse((root / ".ao_checkpoint.json").exists())

    def test_resume_passed_phase_appends_when_entry_missing(self):
        with tempfile.TemporaryDirectory(dir=Path(__file__).parent) as folder:
            root = self.make_project(folder)
            orchestrator.save_checkpoint(root, self.TASK, {}, 0, "the result", "passed", 3)
            self.assertIn("Passed", (root / "HANDOFF.md").read_text(encoding="utf-8"))
            code, _, agent = self.run_main(root)
            self.assertEqual(code, 0)
            agent.assert_not_called()
            state = (root / "STATE.md").read_text(encoding="utf-8")
            self.assertEqual(state.count("## Passed: T-9"), 1)
            self.assertIn("the result", state)

    def test_kill_between_blocked_append_and_clear_resumes_without_duplicate(self):
        with tempfile.TemporaryDirectory(dir=Path(__file__).parent) as folder:
            root = self.make_project(folder)
            self._kill_between(root, ("FAIL",), rounds=0)
            self.assertEqual((root / "ISSUES.md").read_text(encoding="utf-8").count("## T-9 blocked"), 1)
            with self.assertRaises(orchestrator.WorkflowError):
                with patch.object(orchestrator, "run_agent") as agent:
                    self.run_main(root, reviews=("PASS",))
            agent.assert_not_called()
            self.assertEqual((root / "ISSUES.md").read_text(encoding="utf-8").count("## T-9 blocked"), 1)
            self.assertNotIn("## Passed: T-9", (root / "STATE.md").read_text(encoding="utf-8"))
            self.assertFalse((root / ".ao_checkpoint.json").exists())

    def test_snapshot_skips_protected_files_for_globs(self):
        with tempfile.TemporaryDirectory(dir=Path(__file__).parent) as folder:
            root = self.make_project(folder)
            for name in ("HANDOFF.md", ".ao_checkpoint.json", ".ao_checkpoint.json.tmp", "HANDOFF.md.tmp", "notes.md"):
                (root / name).write_text("x\n", encoding="utf-8")
            for pattern in ("*", "*.md"):
                snap = orchestrator.snapshot(root, [pattern])
                self.assertIn("notes.md", snap)
                self.assertFalse(set(snap) & orchestrator.PROTECTED_FILES, snap)

    def test_checkpoint_with_bad_snapshot_values_is_discarded(self):
        for before in ({"a": 5}, {"a": ["x"]}, {"a": {}}):
            with tempfile.TemporaryDirectory(dir=Path(__file__).parent) as folder:
                root = self.make_project(folder)
                (root / ".ao_checkpoint.json").write_text(json.dumps(
                    {"version": 1, "task": self.TASK, "before": before, "round": 0,
                     "reviewer_feedback": "", "phase": "implementing"}), encoding="utf-8")
                code, planner, _ = self.run_main(root)
                self.assertEqual(code, 0)
                planner.assert_called_once()

    def test_models_cannot_write_handoff_or_checkpoint(self):
        with tempfile.TemporaryDirectory(dir=Path(__file__).parent) as folder:
            root = Path(folder)
            for name in ("HANDOFF.md", ".ao_checkpoint.json", ".ao_checkpoint.json.tmp", "HANDOFF.md.tmp"):
                with self.assertRaises(orchestrator.WorkflowError):
                    orchestrator.path_is_allowed(name, ["*"], root)

    def test_project_without_handoff_still_runs(self):
        self.assertNotIn("HANDOFF.md", orchestrator.WORKFLOW_FILES)
        with tempfile.TemporaryDirectory(dir=Path(__file__).parent) as folder:
            root = self.make_project(folder)
            self.assertFalse((root / "HANDOFF.md").exists())
            code, _, _ = self.run_main(root)
            self.assertEqual(code, 0)
            self.assertTrue((root / "HANDOFF.md").exists())

    def test_atomic_temp_files_are_protected_and_hidden(self):
        with tempfile.TemporaryDirectory(dir=Path(__file__).parent) as folder:
            root = self.make_project(folder)
            temps = ("STATE.md.tmp", "ISSUES.md.tmp", "TASK.md.tmp", "HANDOFF.md.tmp", ".ao_checkpoint.json.tmp")
            for name in (*temps, "HANDOFF.md", ".ao_checkpoint.json", "notes.md"):
                (root / name).write_text("x\n", encoding="utf-8")
            snap = orchestrator.snapshot(root, ["*"])
            self.assertIn("notes.md", snap)
            for name in temps:
                self.assertNotIn(name, snap)
                with self.assertRaises(orchestrator.WorkflowError):
                    orchestrator.path_is_allowed(name, ["*"], root)

    def test_planner_inventory_excludes_handoff_checkpoint_and_temp_files(self):
        with tempfile.TemporaryDirectory(dir=Path(__file__).parent) as folder:
            root = self.make_project(folder)
            hidden = ("HANDOFF.md", ".ao_checkpoint.json", ".ao_checkpoint.json.tmp", "HANDOFF.md.tmp",
                      "STATE.md.tmp", "ISSUES.md.tmp")
            for name in (*hidden, "notes.md"):
                (root / name).write_text("x\n", encoding="utf-8")
            context = orchestrator.project_configuration_context(root)
            self.assertIn("notes.md", context)
            for name in hidden:
                self.assertNotIn(name, context.splitlines())

    def test_checkpoint_with_out_of_range_round_is_discarded(self):
        for bad_round in (3, -1, "0"):
            with tempfile.TemporaryDirectory(dir=Path(__file__).parent) as folder:
                root = self.make_project(folder)
                (root / ".ao_checkpoint.json").write_text(json.dumps(
                    {"version": 1, "task": self.TASK, "before": {}, "round": bad_round,
                     "reviewer_feedback": "", "phase": "implementing"}), encoding="utf-8")
                code, planner, _ = self.run_main(root)
                self.assertEqual(code, 0)
                planner.assert_called_once()

    def test_checkpoint_with_unknown_phase_is_discarded(self):
        with tempfile.TemporaryDirectory(dir=Path(__file__).parent) as folder:
            root = self.make_project(folder)
            (root / ".ao_checkpoint.json").write_text(json.dumps(
                {"version": 1, "task": self.TASK, "before": {}, "round": 0,
                 "reviewer_feedback": "", "phase": "dancing"}), encoding="utf-8")
            code, planner, _ = self.run_main(root)
            self.assertEqual(code, 0)
            planner.assert_called_once()

    def test_passed_checkpoint_survives_lowered_round_and_task_limits(self):
        with tempfile.TemporaryDirectory(dir=Path(__file__).parent) as folder:
            root = self.make_project(folder)
            (root / "config.json").write_text(json.dumps({"max_review_rounds": 0, "task_limits": {"max_allowed_files": 1}}), encoding="utf-8")
            task = dict(self.TASK, allowed_files=["a.js", "b.js"])
            orchestrator.save_checkpoint(root, task, {}, 2, "the result", "passed", 3)
            code, planner, agent = self.run_main(root)
            self.assertEqual(code, 0)
            planner.assert_not_called()
            agent.assert_not_called()
            self.assertEqual((root / "STATE.md").read_text(encoding="utf-8").count("## Passed: T-9"), 1)
            self.assertFalse((root / ".ao_checkpoint.json").exists())

    def _states(self, root):
        return (root / "STATE.md").read_text(encoding="utf-8"), (root / "ISSUES.md").read_text(encoding="utf-8")

    def _set_rounds(self, root, rounds):
        (root / "config.json").write_text(json.dumps({"max_review_rounds": rounds}), encoding="utf-8")

    def _block(self, root):
        self._set_rounds(root, 0)
        with self.assertRaises(orchestrator.WorkflowError):
            self.run_main(root, reviews=("FAIL",))

    def test_repeated_block_of_same_id_is_recorded_each_time(self):
        with tempfile.TemporaryDirectory(dir=Path(__file__).parent) as folder:
            root = self.make_project(folder)
            self._block(root)
            self._block(root)
            self.assertEqual(self._states(root)[1].count("## T-9 blocked"), 2)

    def test_repeated_pass_of_same_id_is_recorded_each_time(self):
        with tempfile.TemporaryDirectory(dir=Path(__file__).parent) as folder:
            root = self.make_project(folder)
            self.run_main(root)
            self.run_main(root)
            self.assertEqual(self._states(root)[0].count("## Passed: T-9"), 2)

    def test_pass_block_pass_of_same_id(self):
        with tempfile.TemporaryDirectory(dir=Path(__file__).parent) as folder:
            root = self.make_project(folder)
            self.run_main(root)
            self._block(root)
            self.run_main(root)
            state, issues = self._states(root)
            self.assertEqual(state.count("## Passed: T-9"), 2)
            self.assertEqual(issues.count("## T-9 blocked"), 1)

    def test_complete_task_skips_only_its_own_token(self):
        with tempfile.TemporaryDirectory(dir=Path(__file__).parent) as folder:
            root = self.make_project(folder)
            orchestrator.complete_task(self.TASK, "blocked", "issue", root, "aa11")
            orchestrator.complete_task(self.TASK, "blocked", "issue", root, "aa11")  # re-run of the same completion
            orchestrator.complete_task(self.TASK, "blocked", "issue", root, "bb22")
            self.assertEqual(self._states(root)[1].count("## T-9 blocked"), 2)

    def test_implementer_result_with_passed_heading_does_not_confuse_resume(self):
        body = "did it\n## Passed: OLD-1\nmore"
        with tempfile.TemporaryDirectory(dir=Path(__file__).parent) as folder:
            root = self.make_project(folder)
            (root / "STATE.md").write_text("## Passed: T-9\n- earlier run of the same id\n", encoding="utf-8")
            with patch.object(orchestrator, "clear_checkpoint", side_effect=KeyboardInterrupt):
                with self.assertRaises(KeyboardInterrupt):
                    self.run_main(root, agent=lambda *a: body)
            code, planner, agent = self.run_main(root, reviews=("FAIL",))
            self.assertEqual(code, 0)
            planner.assert_not_called()
            agent.assert_not_called()
            state = self._states(root)[0]
            self.assertEqual(state.count("## Passed: T-9"), 2)  # earlier entry + exactly one new entry
            self.assertEqual(state.count("## Passed: OLD-1"), 1)
            self.assertEqual(state.count("ao-completion"), 1)
            self.assertIn("Last passed task: T-9", (root / "HANDOFF.md").read_text(encoding="utf-8"))

    def test_task_id_with_surrounding_whitespace_resumes_without_duplicate(self):
        task = dict(self.TASK, id=" T-9 ")
        for reviews, rounds in ((("PASS",), None), (("FAIL",), 0)):
            with tempfile.TemporaryDirectory(dir=Path(__file__).parent) as folder:
                root = self.make_project(folder)
                if rounds is not None:
                    self._set_rounds(root, rounds)
                with patch.object(orchestrator, "clear_checkpoint", side_effect=KeyboardInterrupt):
                    with self.assertRaises(KeyboardInterrupt):
                        self.run_main(root, plan={"status": "TASK", "task": task}, reviews=reviews)
                try:
                    self.run_main(root, reviews=("PASS",))
                except orchestrator.WorkflowError:
                    pass
                state, issues = self._states(root)
                self.assertEqual(state.count("## Passed:") + issues.count(" blocked"), 1)

    def test_completion_checkpoint_without_token_is_discarded(self):
        for phase in orchestrator.COMPLETION_PHASES:
            for extra in ({}, {"completion": ""}, {"completion": 5}):
                with tempfile.TemporaryDirectory(dir=Path(__file__).parent) as folder:
                    root = self.make_project(folder)
                    (root / ".ao_checkpoint.json").write_text(json.dumps(
                        {"version": 1, "task": self.TASK, "before": {}, "round": 0,
                         "reviewer_feedback": "fb", "phase": phase, **extra}), encoding="utf-8")
                    code, planner, _ = self.run_main(root, plan={"status": "COMPLETE"})
                    self.assertEqual(code, 0)
                    planner.assert_called_once()
                    self.assertNotIn("fb", "".join(self._states(root)))
                    self.assertFalse((root / ".ao_checkpoint.json").exists())

    def test_non_completion_checkpoint_needs_no_token(self):
        with tempfile.TemporaryDirectory(dir=Path(__file__).parent) as folder:
            root = self.make_project(folder)
            self.assertIsNone(orchestrator.save_checkpoint(root, self.TASK, {}, 0, "", "reviewing", 3))
            self.assertNotIn("completion", json.loads((root / ".ao_checkpoint.json").read_text(encoding="utf-8")))
            self.assertIsNotNone(orchestrator.load_checkpoint(root, (3, 8), 3))

    def test_configure_project_writes_config_atomically(self):
        with tempfile.TemporaryDirectory(dir=Path(__file__).parent) as folder:
            root = self.make_project(folder)
            configured = {"project_configured": True, "validation_commands": {"t": ["x"]}}
            with patch.object(orchestrator, "ask_once", return_value="{}"), patch.object(
                orchestrator, "extract_json_with_repair", return_value=({}, "{}")
            ), patch.object(orchestrator, "apply_project_configuration", return_value=configured), patch.object(
                orchestrator, "write_atomic", wraps=orchestrator.write_atomic
            ) as atomic:
                orchestrator.configure_project({}, root, "proj", False)
            self.assertIn(root / "config.json", [call.args[0] for call in atomic.call_args_list])
            self.assertEqual(json.loads((root / "config.json").read_text(encoding="utf-8"))["project_configured"], True)

    def test_stale_handoff_is_refreshed_when_no_checkpoint(self):
        with tempfile.TemporaryDirectory(dir=Path(__file__).parent) as folder:
            root = self.make_project(folder)
            orchestrator.save_checkpoint(root, self.TASK, {}, 0, "fb", "implementing", 3)
            (root / ".ao_checkpoint.json").unlink()  # killed inside clear_checkpoint
            self.assertIn("In progress", (root / "HANDOFF.md").read_text(encoding="utf-8"))
            code, _, _ = self.run_main(root, plan={"status": "COMPLETE"})
            self.assertEqual(code, 0)
            text = (root / "HANDOFF.md").read_text(encoding="utf-8")
            self.assertIn("No task in progress.", text)
            self.assertNotIn("In progress", text)

    def test_recording_resume_does_not_consume_max_tasks(self):
        with tempfile.TemporaryDirectory(dir=Path(__file__).parent) as folder:
            root = self.make_project(folder)
            (root / "config.json").write_text('{"max_tasks": 1}', encoding="utf-8")
            orchestrator.save_checkpoint(root, self.TASK, {}, 0, "the result", "passed", 3)
            code, planner, _ = self.run_main(root, plan={"status": "COMPLETE"}, once=False)
            self.assertEqual(code, 0)
            planner.assert_called_once()
            self.assertIn("the result", (root / "STATE.md").read_text(encoding="utf-8"))

    def test_handoff_labels_completion_phases(self):
        with tempfile.TemporaryDirectory(dir=Path(__file__).parent) as folder:
            root = self.make_project(folder)
            for phase, label in (("passed", "## Implementer result"), ("blocked", "## Final review"),
                                 ("reviewing", "## Last reviewer feedback")):
                orchestrator.save_checkpoint(root, self.TASK, {}, 0, "body", phase, 3)
                text = (root / "HANDOFF.md").read_text(encoding="utf-8")
                self.assertIn(label, text)
                if phase != "reviewing":
                    self.assertNotIn("Last reviewer feedback", text)

PASS_JSON = json.dumps({"verdict": "PASS", "evidence": ["ok"]})


def fail_json(*defects):
    return json.dumps({"verdict": "FAIL", "defects": [{"file": "a.txt", "defect": d, "expected": "e", "evidence": "v"} for d in defects]})


class CustomWorkerTests(unittest.TestCase):
    """Specialists and Extra reviewers, driven through the real run loop with a fake API client."""

    TASK = OrchestratorTests.TASK | {"allowed_files": ["a.txt"]}
    PROMPTS = {r: (Path(orchestrator.ROOT) / "prompts" / f"{r}.txt").read_text(encoding="utf-8") for r in ("implementer", "reviewer")}

    @staticmethod
    def worker(wid, title, kind="specialist", **extra):
        return {"id": wid, "title": title, "kind": kind, "description": f"{title} does things.", "system_prompt": f"You are {title}.",
                "base_url": "http://x", "expected_model_contains": f"{wid}-model", **extra}

    def config(self, workers=None, **extra):
        roles = {r: {"base_url": "http://x", "expected_model_contains": f"{r}-model"} for r in ("planner", "implementer", "reviewer")}
        return {"roles": roles, **({"custom_workers": workers} if workers is not None else {}), **extra}

    def run_loop(self, root, config, verdicts=None, task=None, once=True):
        """Run orchestrator._run; replies for non-writable roles come from verdicts[role] (default PASS)."""
        (root / "config.json").write_text(json.dumps(config), encoding="utf-8")
        for name in ("PROJECT.md", "STATE.md", "TASK.md", "RESULT.md", "REVIEW.md", "ISSUES.md"):
            if not (root / name).exists():
                (root / name).write_text("Build a real thing\n" if name == "PROJECT.md" else "text\n", encoding="utf-8")
        log, events, queues = [], [], {k: list(v) for k, v in (verdicts or {}).items()}

        class Fake:
            def __init__(self, settings):
                self.settings, self.role = settings, ""

            def ensure_model(self, role, interactive):
                orchestrator.emit("model", role=role, id=self.settings["expected_model_contains"])
                return "m"

            def complete(self, model, messages, sampling, tools=None):
                writable = "write_file" in {t["function"]["name"] for t in tools or []}
                log.append({"role": self.role, "model": self.settings["expected_model_contains"], "system": messages[0]["content"],
                            "user": messages[1]["content"], "writable": writable, "sampling": sampling})
                if writable:
                    if any(m.get("role") == "tool" for m in messages):
                        return {"role": "assistant", "content": "T-9 PASSED"}
                    args = json.dumps({"path": "a.txt", "content": "x"})
                    return {"role": "assistant", "content": "", "tool_calls": [{"id": "c", "type": "function", "function": {"name": "write_file", "arguments": args}}]}
                return {"role": "assistant", "content": queues[self.role].pop(0) if queues.get(self.role) else PASS_JSON}

        argv = ["o", "--project", str(root), "--non-interactive", *(["--once"] if once else [])]
        with patch.object(orchestrator, "ApiClient", Fake), patch.object(orchestrator, "emit", lambda e, **d: events.append((e, d))), \
                patch.object(orchestrator, "request_plan", return_value={"status": "TASK", "task": task or self.TASK}), \
                patch.object(orchestrator, "configure_project", side_effect=lambda c, *a: c), patch.object(orchestrator.sys, "argv", argv):
            code = orchestrator._run()
        return code, log, events

    def test_custom_workers_parsing_ignores_invalid_entries_with_warning(self):
        import contextlib
        import io
        good = [self.worker("s1", "Tester"), self.worker("r1", "Security", "reviewer")]
        bad = [
            self.worker("b1", "Reviewer"), self.worker("b2", "implementer"), self.worker("b3", "PLANNER"),  # reserved titles
            self.worker("b4", "tester"),  # duplicate title (case-insensitive)
            self.worker("s1", "Other"),  # duplicate id
            self.worker("b5", "Odd", "boss"),  # unknown kind
            {k: v for k, v in self.worker("b6", "NoUrl").items() if k != "base_url"},
            {k: v for k, v in self.worker("b7", "NoModel").items() if k != "expected_model_contains"},
            {k: v for k, v in self.worker("b8", "NoPrompt").items() if k != "system_prompt"},
            {k: v for k, v in self.worker("b9", "NoDesc").items() if k != "description"},
            "not an object",
        ]
        config = self.config(good[:1] + bad + good[1:])
        out = io.StringIO()
        with contextlib.redirect_stdout(out):
            parsed = orchestrator.custom_workers(config, warn=True)
        self.assertEqual([w["title"] for w in parsed], ["Tester", "Security"])
        self.assertEqual(out.getvalue().count("[workers] ignoring"), len(bad))
        with contextlib.redirect_stdout(io.StringIO()) as quiet:
            orchestrator.custom_workers(config)
        self.assertEqual(quiet.getvalue(), "")
        self.assertEqual(orchestrator.custom_workers({"custom_workers": "x"}), [])
        # a reviewer needs no description; a model may be named by auto_load alone
        loaded = self.worker("r2", "Lint", "reviewer", auto_load={"model_path": "p"})
        loaded.pop("description"), loaded.pop("expected_model_contains")
        self.assertEqual(len(orchestrator.custom_workers(self.config([loaded]))), 1)

    def test_team_context_lists_workers_and_is_unchanged_without_them(self):
        plain = orchestrator.team_context(self.config())
        self.assertEqual(plain, orchestrator.team_context(self.config([])))
        self.assertNotIn("Specialists", plain)
        text = orchestrator.team_context(self.config([self.worker("s1", "Tester"), self.worker("r1", "Security", "reviewer")]))
        self.assertIn("Specialists", text)
        self.assertIn("- Tester: Tester does things.", text)
        self.assertIn("Extra reviewers", text)
        self.assertIn("- Security: Security does things.", text)
        self.assertEqual(text.splitlines()[-1], plain.splitlines()[-1])  # task limits stay last

    def test_absent_or_empty_array_runs_identically(self):
        runs = []
        for workers in (None, []):
            with tempfile.TemporaryDirectory(dir=Path(__file__).parent) as folder:
                root = Path(folder)
                code, log, events = self.run_loop(root, self.config(workers), {"reviewer": [fail_json("bad"), PASS_JSON]})
                runs.append((code, log, events, (root / "STATE.md").read_text(encoding="utf-8").split("<!--")[0]))
        self.assertEqual(runs[0][:3], runs[1][:3])
        self.assertEqual(runs[0][3], runs[1][3])
        log = runs[0][1]
        self.assertEqual([e["role"] for e in log], ["implementer", "implementer", "reviewer", "implementer", "implementer", "reviewer"])
        self.assertEqual(log[0]["system"], self.PROMPTS["implementer"])
        self.assertEqual(log[2]["system"], self.PROMPTS["reviewer"])

    def test_validate_task_worker_field(self):
        with tempfile.TemporaryDirectory(dir=Path(__file__).parent) as folder:
            root = Path(folder)
            for ok in ({}, {"worker": None}, {"worker": ""}, {"worker": "Tester"}, {"worker": " tester "},
                       {"worker": "Implementer"}, {"worker": " implementer "}):
                orchestrator.validate_task(self.TASK | ok, root, (3, 7), ["Tester"])
            for bad, titles in (("Ghost", ["Tester"]), ("Tester", [])):
                with self.assertRaises(orchestrator.WorkflowError) as ctx:
                    orchestrator.validate_task(self.TASK | {"worker": bad}, root, (3, 7), titles)
                self.assertIn("unknown worker", str(ctx.exception))

    def test_unknown_worker_gets_planner_correction(self):
        bad = {"status": "TASK", "task": self.TASK | {"worker": "Ghost"}}
        good = {"status": "TASK", "task": self.TASK | {"worker": "tester"}}
        config = self.config([self.worker("s1", "Tester")])
        with tempfile.TemporaryDirectory(dir=Path(__file__).parent) as folder:
            with patch.object(orchestrator, "ask_once", side_effect=[json.dumps(bad), json.dumps(good)]) as planner:
                plan = orchestrator.request_plan(config, "system", "user", Path(folder), False)
        self.assertEqual(plan["task"]["worker"], "tester")
        self.assertEqual(planner.call_count, 2)
        self.assertIn("unknown worker 'Ghost'", planner.call_args.args[3])

    def test_specialist_does_the_task_instead_of_the_implementer(self):
        tester = self.worker("s1", "Tester", sampling={"max_tokens": 123, "extra_body": {"top_k": 7}})
        with tempfile.TemporaryDirectory(dir=Path(__file__).parent) as folder:
            root = Path(folder)
            code, log, events = self.run_loop(root, self.config([tester]), task=self.TASK | {"worker": "tester"})
            self.assertEqual(code, 0)
            self.assertEqual((root / "a.txt").read_text(encoding="utf-8"), "x\n")
            self.assertEqual(len((root / "STATE.md").read_text(encoding="utf-8").split("## Passed: T-9")), 2)
        self.assertEqual([e["role"] for e in log], ["s1", "s1", "reviewer"])  # tool call + report, then the main reviewer
        work = log[0]
        self.assertEqual(work["model"], "s1-model")
        self.assertTrue(work["writable"])
        self.assertEqual((work["sampling"]["max_tokens"], work["sampling"]["top_k"]), (123, 7))
        self.assertTrue(work["system"].startswith(self.PROMPTS["implementer"].rstrip()))
        self.assertIn("YOUR ROLE: Tester", work["system"])
        self.assertTrue(work["system"].rstrip().endswith("You are Tester."))
        self.assertEqual(log[2]["system"], self.PROMPTS["reviewer"])
        self.assertIn(("model", {"role": "s1", "id": "s1-model"}), events)
        self.assertEqual([d["role"] for e, d in events if e == "tool"], ["s1"])
        self.assertNotIn("implementer", {d.get("role") for e, d in events})

    def test_specialist_choice_survives_resume(self):
        tester = self.worker("s1", "Tester")
        task = self.TASK | {"worker": "Tester"}
        with tempfile.TemporaryDirectory(dir=Path(__file__).parent) as folder:
            root = Path(folder)
            orchestrator.save_checkpoint(root, task, {"a.txt": None}, 0, "fb", "implementing", 3)
            code, log, _ = self.run_loop(root, self.config([tester]), task=dict(self.TASK, id="OTHER"))
            self.assertEqual(code, 0)
            self.assertEqual(log[0]["role"], "s1")  # the checkpointed task, not the planner's, ran
            self.assertIn("## Passed: T-9", (root / "STATE.md").read_text(encoding="utf-8"))

    def test_plan_stores_worker_id_and_implementer_means_none(self):
        config = self.config([self.worker("s1", "Tester")])
        with tempfile.TemporaryDirectory(dir=Path(__file__).parent) as folder:
            for name, expect in (("tester", "s1"), ("Implementer", None), (None, None)):
                plan = {"status": "TASK", "task": self.TASK | {"worker": name}}
                with patch.object(orchestrator, "ask_once", return_value=json.dumps(plan)):
                    task = orchestrator.request_plan(config, "s", "u", Path(folder), False)["task"]
                self.assertEqual(task.get("worker_id"), expect)

    def test_renamed_specialist_resumes_by_id_and_removed_one_falls_back(self):
        task = self.TASK | {"worker": "Tester", "worker_id": "s1"}
        before = {"a.txt": None}
        with tempfile.TemporaryDirectory(dir=Path(__file__).parent) as folder:
            root = Path(folder)
            orchestrator.save_checkpoint(root, task, before, 1, "fb", "implementing", 3)
            renamed = self.config([self.worker("s1", "QA Lead")])
            code, log, _ = self.run_loop(root, renamed, task=dict(self.TASK, id="OTHER"))
            self.assertEqual((code, log[0]["role"]), (0, "s1"))
            self.assertIn("YOUR ROLE: QA Lead", log[0]["system"])
            self.assertIn("## Passed: T-9", (root / "STATE.md").read_text(encoding="utf-8"))
            # round and snapshot survive the rename
            orchestrator.save_checkpoint(root, task, before, 1, "fb", "implementing", 3)
            data = orchestrator.load_checkpoint(root, (3, 7), 3)
            self.assertEqual((data["round"], data["before"], data["task"]["worker_id"]), (1, before, "s1"))
            # Specialist removed (or turned into a reviewer): the Implementer continues, checkpoint kept
            for workers in ([], [self.worker("s1", "Tester", "reviewer")]):
                out = io.StringIO()
                with contextlib.redirect_stdout(out):
                    self.assertIsNone(orchestrator.find_specialist(data["task"], orchestrator.custom_workers(self.config(workers))))
                self.assertIn("no longer exists", out.getvalue())
            orchestrator.save_checkpoint(root, task, before, 1, "fb", "implementing", 3)
            out = type("Out", (io.StringIO,), {"reconfigure": lambda self, **kw: None})()
            with contextlib.redirect_stdout(out):
                code, log, _ = self.run_loop(root, self.config([]), task=dict(self.TASK, id="OTHER"))
            self.assertEqual((code, log[0]["role"]), (0, "implementer"))
            self.assertIn("no longer exists", out.getvalue())
            self.assertNotIn("discarding", out.getvalue())

    def test_log_lines_show_title_but_events_keep_id(self):
        tester = self.worker("s1-guid", "Tester")
        with patch.dict(os.environ, {"AO_TEST_KEY": "k"}):
            settings = dict(tester, api_key_env="AO_TEST_KEY")
            client = orchestrator.ApiClient(settings)
            client.title = orchestrator.display_name(self.config([tester]), "s1-guid")
            events = []
            out = io.StringIO()
            with patch.object(client, "model_id", return_value="s1-guid-model"), patch.object(orchestrator, "emit", lambda e, **d: events.append((e, d))), \
                    contextlib.redirect_stdout(out):
                client.ensure_model("s1-guid", False)
        self.assertIn("[Tester] s1-guid-model", out.getvalue())
        self.assertNotIn("[s1-guid]", out.getvalue())
        self.assertEqual(events, [("model", {"role": "s1-guid", "id": "s1-guid-model"})])

    def reviewers(self):
        return [self.worker("r1", "Security", "reviewer"), self.worker("r2", "Perf", "reviewer")]

    def test_extra_reviewer_fail_sends_task_back_with_titled_feedback(self):
        with tempfile.TemporaryDirectory(dir=Path(__file__).parent) as folder:
            root = Path(folder)
            verdicts = {"r1": [fail_json("SQL injection"), PASS_JSON]}
            code, log, events = self.run_loop(root, self.config(self.reviewers()), verdicts)
            self.assertEqual(code, 0)
            self.assertEqual([e["role"] for e in log if not e["writable"]], ["reviewer", "r1", "r2", "reviewer", "r1", "r2"])
            second = [e for e in log if e["writable"]][-1]  # the round-2 implementer's last request
            self.assertEqual(sum("Security: SQL injection" in e["user"] for e in log if e["role"] == "implementer"), 2)
            self.assertIn("REVIEWER FEEDBACK\n{", second["user"])
            self.assertEqual([d["verdict"] for e, d in events if e == "verdict"], ["FAIL", "PASS"])
            self.assertEqual(len((root / "STATE.md").read_text(encoding="utf-8").split("## Passed: T-9")), 2)

    def test_extra_reviewer_fail_is_recorded_in_review_md_and_blocks_at_the_limit(self):
        with tempfile.TemporaryDirectory(dir=Path(__file__).parent) as folder:
            root = Path(folder)
            verdicts = {"r1": [fail_json("SQL injection")], "r2": [fail_json("slow loop", "n+1")]}
            with self.assertRaises(orchestrator.WorkflowError):
                self.run_loop(root, self.config(self.reviewers(), max_review_rounds=0), verdicts)
            review = json.loads((root / "REVIEW.md").read_text(encoding="utf-8"))
            self.assertEqual(review["verdict"], "FAIL")
            self.assertEqual([d["defect"] for d in review["defects"]], ["Security: SQL injection", "Perf: slow loop", "Perf: n+1"])
            self.assertIn("Security: SQL injection", (root / "ISSUES.md").read_text(encoding="utf-8"))
            self.assertNotIn("## Passed: T-9", (root / "STATE.md").read_text(encoding="utf-8"))

    def test_extra_reviewers_all_pass_records_once_with_reviewer_rules(self):
        with tempfile.TemporaryDirectory(dir=Path(__file__).parent) as folder:
            root = Path(folder)
            code, log, events = self.run_loop(root, self.config(self.reviewers()))
            self.assertEqual(code, 0)
            self.assertEqual(len((root / "STATE.md").read_text(encoding="utf-8").split("## Passed: T-9")), 2)
        extras = [e for e in log if e["role"] in ("r1", "r2")]
        self.assertEqual([e["role"] for e in extras], ["r1", "r2"])
        for e in extras:
            self.assertFalse(e["writable"])
            self.assertTrue(e["system"].startswith(self.PROMPTS["reviewer"].rstrip()))
            self.assertIn("YOUR ROLE:", e["system"])
            self.assertIn("IMPLEMENTER RESULT", e["user"])
        self.assertEqual([d["verdict"] for e, d in events if e == "verdict"], ["PASS"])

    def test_main_reviewer_fail_skips_extra_reviewers_that_round(self):
        with tempfile.TemporaryDirectory(dir=Path(__file__).parent) as folder:
            _, log, _ = self.run_loop(Path(folder), self.config(self.reviewers()), {"reviewer": [fail_json("nope"), PASS_JSON]})
        self.assertEqual([e["role"] for e in log if not e["writable"]], ["reviewer", "reviewer", "r1", "r2"])

    def test_extra_reviewer_verdict_repair_uses_its_own_model(self):
        with tempfile.TemporaryDirectory(dir=Path(__file__).parent) as folder:
            _, log, _ = self.run_loop(Path(folder), self.config(self.reviewers()[:1]), {"r1": ["no json here", PASS_JSON]})
        self.assertEqual([(e["role"], e["model"]) for e in log if e["role"] == "r1"], [("r1", "r1-model")] * 2)

    def test_resume_in_extra_review_phase_skips_implementer_and_records_once(self):
        with tempfile.TemporaryDirectory(dir=Path(__file__).parent) as folder:
            root = Path(folder)
            orchestrator.save_checkpoint(root, self.TASK, {"a.txt": None}, 0, "fb", "extra_reviewing", 3)
            (root / "a.txt").write_text("x\n", encoding="utf-8")
            (root / "RESULT.md").write_text("earlier result\n", encoding="utf-8")
            (root / "REVIEW.md").write_text(PASS_JSON, encoding="utf-8")
            self.assertIn("Extra reviewers checking", (root / "HANDOFF.md").read_text(encoding="utf-8"))
            config = self.config(self.reviewers())
            # killed right after the completion entry is written: the next run only clears the checkpoint
            with patch.object(orchestrator, "clear_checkpoint", side_effect=KeyboardInterrupt):
                with self.assertRaises(KeyboardInterrupt):
                    self.run_loop(root, config)
            self.assertEqual(json.loads((root / ".ao_checkpoint.json").read_text(encoding="utf-8"))["phase"], "passed")
            state = (root / "STATE.md").read_text(encoding="utf-8")
            code, log, _ = self.run_loop(root, config)
            self.assertEqual(code, 0)
            self.assertEqual(log, [])  # recording only
            self.assertEqual((root / "STATE.md").read_text(encoding="utf-8"), state)
            self.assertEqual(state.count("## Passed: T-9"), 1)
            self.assertFalse((root / ".ao_checkpoint.json").exists())

    def test_resume_in_extra_review_phase_runs_only_the_extra_reviewers(self):
        with tempfile.TemporaryDirectory(dir=Path(__file__).parent) as folder:
            root = Path(folder)
            orchestrator.save_checkpoint(root, self.TASK, {"a.txt": None}, 0, "fb", "extra_reviewing", 3)
            (root / "a.txt").write_text("x\n", encoding="utf-8")
            (root / "RESULT.md").write_text("earlier result\n", encoding="utf-8")
            (root / "REVIEW.md").write_text(PASS_JSON, encoding="utf-8")
            code, log, _ = self.run_loop(root, self.config(self.reviewers()), {"r2": [fail_json("late"), PASS_JSON]})
            self.assertEqual(code, 0)
            self.assertEqual([e["role"] for e in log if not e["writable"]], ["r1", "r2", "reviewer", "r1", "r2"])
            self.assertIn("earlier result", log[0]["user"])  # the resumed round reviews the saved result
            self.assertEqual(sum("## Passed: T-9" in l for l in (root / "STATE.md").read_text(encoding="utf-8").splitlines()), 1)


if __name__ == "__main__":
    unittest.main()
