import json
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

if __name__ == "__main__":
    unittest.main()
