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
                "id": "T-1", "outcome": "shell", "allowed_files": ["a", "b", "c", "d", "e", "f"],
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

    def test_extract_json_tolerates_prose_and_lossless_syntax_slips(self):
        cases = [
            ('Here is the plan:\n```json\n{"status": "COMPLETE"}\n```\nThat is all.', {"status": "COMPLETE"}),
            ('<think>maybe {"status": "TASK"</think>\n{"status": "COMPLETE"}', {"status": "COMPLETE"}),
            ('{"a": [1, 2,], "b": {"c": 3,},}', {"a": [1, 2], "b": {"c": 3}}),
            ('{"a": 1, // note {\n "b": "http://x" /* c */}', {"a": 1, "b": "http://x"}),
            ('{“a”: “b”, "c": "it’s “ok”"}', {"a": "b", "c": "it’s “ok”"}),
            ('{"a": "line1\nline2\tend"}', {"a": "line1\nline2\tend"}),
            ('Draft: {"verdict": "PASS"}\nFinal answer: {"verdict": "FAIL", "defects": []}', {"verdict": "FAIL", "defects": []}),
        ]
        for text, expected in cases:
            with self.subTest(text=text):
                self.assertEqual(orchestrator.extract_json(text), expected)
        # Truncated or absent JSON is never "fixed" by closing it or filling keys.
        for text in ('{"status": "TASK", "task": {', '{"verdict": "PASS"} then {"verdict": "FA', "", "<think>planning {", "no json"):
            with self.subTest(text=text), self.assertRaises(orchestrator.WorkflowError):
                orchestrator.extract_json(text)

    def test_cut_off_plan_is_re_asked_without_thinking_not_repaired(self):
        class Client:
            def __init__(self):
                self.calls, self.cut_off = [], False

            def ensure_model(self, role, interactive):
                return "m"

            def complete(self, model, messages, sampling, tools=None):
                self.calls.append((messages, sampling))
                self.cut_off = len(self.calls) == 1
                return {"role": "assistant", "content": '<think>long</think>{"status": "TASK", "task": {"id": "T-1", "outc'
                        if self.cut_off else '{"status": "COMPLETE"}'}

        client, out = Client(), io.StringIO()
        with patch.object(orchestrator, "role_settings", return_value=(client, {"max_tokens": 8192})), contextlib.redirect_stdout(out):
            plan = orchestrator.request_plan({}, "SYS", "USER", Path("."), False)
        self.assertEqual(plan, {"status": "COMPLETE"})
        self.assertEqual(len(client.calls), 2)
        self.assertNotIn("enable_thinking", client.calls[0][1])
        self.assertIs(client.calls[1][1]["enable_thinking"], False)
        self.assertEqual(client.calls[1][0][0]["content"], "SYS")
        self.assertTrue(client.calls[1][0][1]["content"].startswith("USER\n"))
        self.assertNotIn("MALFORMED", client.calls[1][0][1]["content"])
        self.assertIn("[planner] reply cut off / no JSON; re-asking without thinking", out.getvalue())

    def test_malformed_json_repairs_keep_the_contract_then_one_fresh_attempt(self):
        with patch.object(orchestrator, "ask_once", return_value='{"status": TASK}') as ask, contextlib.redirect_stdout(io.StringIO()):
            with self.assertRaisesRegex(orchestrator.WorkflowError, "planner.*restarted"):
                orchestrator.extract_json_with_repair({}, "planner", '{"status": TASK}', False, system="SYS", user="USER")
        self.assertEqual(ask.call_count, 3)  # two repairs, one fresh attempt
        for call in ask.call_args_list:
            self.assertIs(call.kwargs["thinking"], False)
        repair, fresh = ask.call_args_list[0].args, ask.call_args_list[2].args
        self.assertTrue(repair[2].startswith("SYS\n"))
        self.assertIn("MALFORMED RESPONSE", repair[3])
        self.assertEqual(fresh[2], "SYS")
        self.assertTrue(fresh[3].startswith("USER\n"))

    def test_unreadable_verdict_fails_the_round_and_the_loop_goes_on(self):
        with tempfile.TemporaryDirectory(dir=Path(__file__).parent) as folder:
            root = self.make_project(folder)
            task = dict(self.TASK, allowed_files=["a.txt"])
            (root / "TASK.md").write_text("task\n", encoding="utf-8")
            feedback, reviews = [], iter(["garbage, no verdict", '{"verdict": "PASS", "evidence": ["ok"]}'])

            def agent(config, role, system, user, *rest):
                if role == "reviewer":
                    return next(reviews)
                feedback.append(user)
                (root / "a.txt").write_text(f"v{len(feedback)}\n", encoding="utf-8")
                return "done"

            with patch.object(orchestrator, "run_agent", side_effect=agent), patch.object(orchestrator, "ask_once", return_value="still garbage"), \
                    patch.object(orchestrator, "emit"), contextlib.redirect_stdout(io.StringIO()):
                orchestrator.run_review_loop({}, {"implementer": "", "reviewer": ""}, task, {"a.txt": None}, 0, "", "implementing", 2, root, False)
            self.assertEqual(len(feedback), 2)
            self.assertIn(orchestrator.UNREADABLE_VERDICT, feedback[1])
            self.assertIn("garbage, no verdict", feedback[1])  # the raw reply reaches REVIEW.md / the next round
            # Garbage every round never passes: the task ends blocked by the ordinary review-round limit.
            with patch.object(orchestrator, "run_agent", side_effect=lambda c, role, *a: "junk" if role == "reviewer" else "done"), \
                    patch.object(orchestrator, "ask_once", return_value="junk"), patch.object(orchestrator, "emit"), \
                    contextlib.redirect_stdout(io.StringIO()):
                with self.assertRaisesRegex(orchestrator.WorkflowError, "failed after 2 reviews"):
                    orchestrator.run_review_loop({}, {"implementer": "", "reviewer": ""}, task, {"a.txt": None}, 0, "", "implementing", 1, root, False)
            self.assertIn(orchestrator.UNREADABLE_VERDICT, (root / "REVIEW.md").read_text(encoding="utf-8"))

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

    def test_loading_event_carries_management_url(self):
        client, _ = self._managed_client({"requested_context_length": 32768, "requested_reasoning_budget": 8192})
        with patch.object(orchestrator, "LOADED_BY_RUN", {}), patch.object(orchestrator, "emit") as emit:
            client.ensure_model("implementer", False)
        emit.assert_any_call("loading", role="implementer", path="u/M", url=client.management_url)
        self.assertEqual(client.management_url, "http://m")

    def test_answer_text_strips_every_think_block_and_only_a_trailing_open_one(self):
        def answer(content):
            return orchestrator.answer_text({"content": content})
        self.assertEqual(answer("<think>a</think>one <think>b</think>two"), "one two")
        self.assertEqual(answer("pre <think>a</think> mid <think>b</think> post"), "pre  mid  post")
        self.assertEqual(answer("keep <think>x</think> and <think>y</think> this"), "keep  and  this")
        self.assertEqual(answer("answer<think>unfinished"), "answer")
        self.assertEqual(answer("<think>a</think>one <think>b</think>two <think>open"), "one two")

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

    def test_has_output_keeps_text_before_a_literal_think_tag(self):
        msg = {"content": "Use the <think> tag like this <think>scratch</think> and more text"}
        self.assertTrue(orchestrator.has_output(msg))
        self.assertTrue(orchestrator.has_output({"content": "answer then <think>unfinished"}))
        self.assertFalse(orchestrator.has_output({"content": "<think>only reasoning"}))
        # two reasoning blocks before the answer: the answer must still count as output
        self.assertTrue(orchestrator.has_output({"content": "<think>a</think><think>b</think>real answer"}))

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


    def test_prior_work_without_new_writes_reaches_reviewer(self):
        class Client:
            completions = 0
            messages = None

            def ensure_model(self, role, interactive):
                return "m"

            def complete(self, model, messages, sampling, tools=None):
                self.completions += 1
                self.messages = [dict(m) for m in messages]
                return {"role": "assistant", "content": "The feature is already implemented."}

        with tempfile.TemporaryDirectory(dir=Path(__file__).parent) as folder:
            root = self.make_project(folder)
            task = dict(self.TASK, allowed_files=["a.txt"])
            (root / "TASK.md").write_text("task\n", encoding="utf-8")
            (root / "a.txt").write_text("new\n", encoding="utf-8")
            client, reviewer = Client(), []

            def agent(config, role, system, user, *rest):
                if role == "reviewer":
                    reviewer.append(user)
                    return '{"verdict":"PASS"}'
                with patch.object(orchestrator, "role_settings", return_value=(client, {})):
                    return REAL_RUN_AGENT(config, role, system, user, *rest)

            with patch.object(orchestrator, "run_agent", side_effect=agent), patch.object(orchestrator, "emit"):
                orchestrator.run_review_loop({}, {"implementer": "", "reviewer": ""}, task, {"a.txt": "old\n"}, 0, "", "implementing", 1, root, False)
            self.assertEqual(client.completions, 1)  # no "No file was changed" nudge
            self.assertIn("PRIOR WORK", client.messages[1]["content"])
            self.assertNotIn("BLOCKED", (root / "RESULT.md").read_text(encoding="utf-8"))
            self.assertEqual(len(reviewer), 1)
            self.assertIn("-old", reviewer[0])
            self.assertIn("+new", reviewer[0])
            # Files equal to the task-start snapshot: nothing was ever written, so the block still fires.
            with patch.object(orchestrator, "role_settings", return_value=(Client(), {})), patch.object(orchestrator, "emit"):
                with self.assertRaises(orchestrator.WorkflowError) as ctx:
                    REAL_RUN_AGENT({}, "implementer", "s", "u", root, ["a.txt"], True, False, {"a.txt": "new\n"})
            self.assertIn("never executed a tool", str(ctx.exception))

    def test_truncated_tool_call_is_not_executed_and_repeats_fail(self):
        cut = {"role": "assistant", "content": "", "tool_calls": [{"id": "c", "function": {"name": "write_file", "arguments": '{"path": "b.txt", "content": "hel'}}]}

        class Client:
            completions = 0

            def __init__(self, replies):
                self.replies, self.cut_off, self.messages = replies, False, None

            def ensure_model(self, role, interactive):
                return "m"

            def complete(self, model, messages, sampling, tools=None):
                self.completions += 1
                self.messages = [dict(m) for m in messages]
                reply = self.replies[min(self.completions - 1, len(self.replies) - 1)]
                self.cut_off = reply is cut
                return reply

        with tempfile.TemporaryDirectory(dir=Path(__file__).parent) as folder:
            root = Path(folder)
            client = Client([cut, self._native("write_file", path="a.txt", content="x"), {"role": "assistant", "content": "done"}])
            with patch.object(orchestrator, "role_settings", return_value=(client, {})), patch.object(orchestrator, "emit"):
                result = REAL_RUN_AGENT({}, "implementer", "s", "u", root, ["a.txt", "b.txt"], True, False)
            self.assertEqual(result, "done")
            self.assertFalse((root / "b.txt").exists())
            self.assertIn("cut off at the output limit", client.messages[3]["content"])
            self.assertNotIn("tool_calls", client.messages[2])  # the broken arguments are not replayed
            client = Client([cut])
            with patch.object(orchestrator, "role_settings", return_value=(client, {})), patch.object(orchestrator, "emit"):
                with self.assertRaises(orchestrator.WorkflowError) as ctx:
                    REAL_RUN_AGENT({}, "implementer", "s", "u", root, ["a.txt", "b.txt"], True, False)
            self.assertIn("max_tokens", str(ctx.exception))
            self.assertIn("implementer", str(ctx.exception))
            self.assertEqual(client.completions, 3)
            self.assertFalse((root / "b.txt").exists())

    def test_complete_reports_a_reply_cut_off_with_output(self):
        client = orchestrator.ApiClient({"base_url": "http://x/v1", "api_key": "k"})
        client.role = "implementer"
        client._request = lambda method, route, body=None: {"choices": [{"message": {"content": "partial"}, "finish_reason": "length"}]}
        out = io.StringIO()
        with patch.object(orchestrator, "EVENTS", False), contextlib.redirect_stdout(out):
            client.complete("m", [], {"max_tokens": 5})
        self.assertTrue(client.cut_off)
        self.assertIn("[implementer] reply cut off at the output limit (max_tokens=5)", out.getvalue())

    def test_context_overflow_names_max_seq_length(self):
        from urllib.error import HTTPError

        def overflow(*args, **kwargs):
            raise HTTPError("http://x", 400, "Bad Request", {}, io.BytesIO(b'{"error":{"message":"Prompt is too long for the context window"}}'))

        def other(*args, **kwargs):
            raise HTTPError("http://x", 400, "Bad Request", {}, io.BytesIO(b'{"error":{"message":"bad temperature"}}'))

        client = orchestrator.ApiClient({"base_url": "http://x/v1", "api_key": "k"})
        client.role = "reviewer"
        for events in (False, True):
            with patch.object(orchestrator, "EVENTS", events), patch.object(orchestrator, "urlopen", side_effect=overflow):
                with self.assertRaises(orchestrator.WorkflowError) as ctx:
                    client.complete("m", [], {})
            self.assertIn("reviewer", str(ctx.exception))
            self.assertIn("auto_load.max_seq_length", str(ctx.exception))
        with patch.object(orchestrator, "EVENTS", False), patch.object(orchestrator, "urlopen", side_effect=other):
            with self.assertRaises(orchestrator.WorkflowError) as ctx:
                client.complete("m", [], {})
        self.assertIn("API 400", str(ctx.exception))

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

    BROKEN_PAGE = {"errors": [{"message": "Uncaught TypeError: components is not iterable", "source": "app.js", "line": 3}], "text": "", "visible": 0}

    def _behaviour_loop(self, report, task=None):
        """Run one task (2 rounds max) on a web project with the browser faked; returns [(role, user input)]."""
        inputs = []
        task = task or self.TASK

        def agent(config, role, system, user, *rest):
            inputs.append((role, user))
            return '{"verdict":"PASS"}' if role == "reviewer" else "PASSED"

        with tempfile.TemporaryDirectory(dir=Path(__file__).parent) as folder:
            root = self.make_project(folder)
            (root / "index.html").write_text("<html></html>", encoding="utf-8")
            (root / "TASK.md").write_text("task\n", encoding="utf-8")
            with patch.object(orchestrator, "run_agent", side_effect=agent), patch.object(orchestrator, "emit"), \
                    patch.object(orchestrator, "find_browser", return_value="browser"), \
                    patch.object(orchestrator, "load_page", return_value=report) as self.load, contextlib.redirect_stdout(io.StringIO()):
                try:
                    orchestrator.run_review_loop({}, {"implementer": "", "reviewer": ""}, task, {}, 0, "", "implementing", 1, root, False)
                except orchestrator.WorkflowError:
                    pass
            review = json.loads((root / "REVIEW.md").read_text(encoding="utf-8"))
        return inputs, review

    def test_page_errors_fail_round_without_reviewer_and_go_back_to_implementer(self):
        inputs, review = self._behaviour_loop(self.BROKEN_PAGE)
        self.assertEqual([role for role, _ in inputs], ["implementer", "implementer"])
        self.assertEqual(review["verdict"], "FAIL")
        self.assertEqual(review["defects"][0]["evidence"], "Uncaught TypeError: components is not iterable (app.js:3)")
        self.assertIn("components is not iterable (app.js:3)", inputs[1][1].split("REVIEWER FEEDBACK", 1)[1])

    def test_clean_page_gives_reviewer_behaviour_evidence(self):
        inputs, review = self._behaviour_loop({"errors": [], "text": "PC Tracker\nAdd component", "visible": 7})
        reviewer = [user for role, user in inputs if role == "reviewer"]
        self.assertEqual(len(reviewer), 1)
        self.assertIn("BEHAVIOUR CHECK: passed (index.html", reviewer[0])
        self.assertIn("Errors: none", reviewer[0])
        self.assertIn("PC Tracker\nAdd component", reviewer[0])
        self.assertEqual(review["verdict"], "PASS")

    STEPS = [{"do": "click", "selector": "#save-build"}, {"expect": "text", "selector": "#build-list", "contains": "Gaming PC"},
             {"expect": "no_errors"}]

    def test_failed_acceptance_step_fails_round_without_reviewer(self):
        report = {"errors": [], "text": "Error saving build", "visible": 5, "steps": [
            {"index": 1, "step": self.STEPS[0], "ok": True, "detail": ""},
            {"index": 2, "step": self.STEPS[1], "ok": False, "detail": "expected text 'Gaming PC' in #build-list; got ''"},
            {"index": 3, "step": self.STEPS[2], "ok": None, "detail": "not run"}]}
        inputs, review = self._behaviour_loop(report, dict(self.TASK, acceptance=self.STEPS))
        self.assertEqual(self.load.call_args.kwargs["steps"], self.STEPS)
        self.assertEqual([role for role, _ in inputs], ["implementer", "implementer"])
        self.assertEqual(review["defects"], [{
            "file": "index.html", "defect": "Acceptance step failed", "expected": "Every acceptance step passes",
            "evidence": 'acceptance step 2 failed: {"expect": "text", "selector": "#build-list", "contains": "Gaming PC"}'
                        " — expected text 'Gaming PC' in #build-list; got ''"}])
        self.assertIn("expected text 'Gaming PC' in #build-list; got ''", inputs[1][1].split("REVIEWER FEEDBACK", 1)[1])

    def test_passing_acceptance_steps_reach_reviewer_and_missing_steps_are_noted(self):
        report = {"errors": [], "text": "Gaming PC", "visible": 5,
                  "steps": [{"index": n, "step": s, "ok": True, "detail": ""} for n, s in enumerate(self.STEPS, 1)]}
        inputs, review = self._behaviour_loop(report, dict(self.TASK, acceptance=self.STEPS))
        reviewer = [user for role, user in inputs if role == "reviewer"]
        self.assertEqual(len(reviewer), 1)
        self.assertIn('Acceptance steps (3/3 passed):\n✓ 1. {"do": "click", "selector": "#save-build"}\n✓ 2.', reviewer[0])
        self.assertIn("Visible text sample:\nGaming PC", reviewer[0])
        self.assertEqual(review["verdict"], "PASS")
        inputs, _ = self._behaviour_loop({"errors": [], "text": "x", "visible": 1, "steps": None})
        self.assertIsNone(self.load.call_args.kwargs["steps"])
        self.assertIn("no acceptance steps were given", [user for role, user in inputs if role == "reviewer"][0])

    def test_acceptance_steps_validated_and_shown_in_task_markdown(self):
        all_forms = self.STEPS + [
            {"do": "fill", "selector": "#n", "value": "Gaming PC"}, {"do": "select", "selector": "#c", "value": 3},
            {"do": "check", "selector": "#f", "value": False}, {"do": "goto", "hash": "#builds"}, {"do": "wait", "ms": 2000},
            {"expect": "visible", "selector": "#a"}, {"expect": "hidden", "selector": "#b"},
            {"expect": "count", "selector": "li", "min": 0, "max": 2}, {"expect": "storage", "key": "builds", "json_length_min": 1}]
        bad = [
            "click", {"steps": []}, [{"do": "hover", "selector": "#a"}], [{"expect": "title"}], [{"do": "click", "expect": "text"}],
            [{"do": "click"}], [{"do": "click", "selector": ""}], [{"do": "click", "selector": "#" + "a" * 200}],
            [{"do": "fill", "selector": "#n"}], [{"do": "check", "selector": "#f", "value": "yes"}], [{"do": "wait", "ms": 2001}],
            [{"expect": "text", "selector": "#l"}], [{"expect": "count", "selector": "li"}], [{"expect": "count", "selector": "li", "min": 1, "max": "2"}],
            [{"expect": "storage", "key": "b", "json_length_min": True}], [{"do": ["click"], "selector": "#a"}], ["click #a"],
            [{"expect": "no_errors"}] * 13,
        ]
        with tempfile.TemporaryDirectory(dir=Path(__file__).parent) as folder:
            root = Path(folder)
            for ok in ([], self.STEPS, all_forms):
                orchestrator.validate_task(dict(self.TASK, acceptance=ok), root)
            orchestrator.validate_task(self.TASK, root)
            for steps in bad:
                with self.subTest(steps=steps), self.assertRaisesRegex(orchestrator.WorkflowError, "acceptance"):
                    orchestrator.validate_task(dict(self.TASK, acceptance=steps), root)
        markdown = orchestrator.task_markdown(dict(self.TASK, acceptance=self.STEPS))
        self.assertIn("## Acceptance steps\n", markdown)
        self.assertIn("part of the contract", markdown)
        self.assertIn('\n1. `{"do": "click", "selector": "#save-build"}`\n2. `{"expect": "text"', markdown)
        self.assertNotIn("Acceptance", orchestrator.task_markdown(self.TASK))

    def test_acceptance_steps_round_trip_through_checkpoint(self):
        with tempfile.TemporaryDirectory(dir=Path(__file__).parent) as folder:
            root = self.make_project(folder)
            orchestrator.save_checkpoint(root, dict(self.TASK, acceptance=self.STEPS), {}, 0, "", "implementing", 3)
            self.assertEqual(orchestrator.load_checkpoint(root, (3, 7), 3)["task"]["acceptance"], self.STEPS)

    def test_probe_escapes_steps_inside_the_script(self):
        probe, budget = orchestrator.behaviour_probe([{"expect": "text", "selector": "#a", "contains": "</script><!--  "},
                                                      {"do": "wait", "ms": 2000}])
        self.assertEqual(probe.count("</script>"), 1)
        self.assertTrue(probe.endswith("</script>"))
        self.assertNotIn("<!--", probe)
        self.assertNotIn(" ", probe)
        self.assertNotIn(" ", probe)
        self.assertEqual(budget, 5000 + 800 + 2000)
        self.assertEqual(orchestrator.behaviour_probe([{"expect": "no_errors"}] * 40)[1], 15000)
        self.assertIn("S=null;", orchestrator.behaviour_probe(None)[0])

    def test_planner_sees_behaviour_check_of_current_project(self):
        with tempfile.TemporaryDirectory(dir=Path(__file__).parent) as folder:
            root = self.make_project(folder)
            (root / "index.html").write_text("<html></html>", encoding="utf-8")
            seen = []

            def on_plan(config, system, user, *rest):
                seen.append(user)
                return {"status": "COMPLETE", "summary": "done"}

            with patch.object(orchestrator, "find_browser", return_value="browser"), \
                    patch.object(orchestrator, "load_page", return_value=self.BROKEN_PAGE) as load:
                self.run_main(root, on_plan=on_plan)
            self.assertIsNone(load.call_args.kwargs["steps"])  # load-only before planning
            self.assertIn("BEHAVIOUR CHECK OF CURRENT PROJECT\nBEHAVIOUR CHECK: FAILED (index.html)", seen[0])
            self.assertIn("components is not iterable (app.js:3)", seen[0])
            self.assertIn("BEHAVIOUR CHECK OF CURRENT PROJECT", orchestrator.load_text(orchestrator.ROOT / "prompts" / "planner.txt"))

    DATA_LITERAL = "window.DataStore = {\n  getComponents() { return []; },\n  createBuild: function (b) { return b; },\n  exportBuilds: () => '',\n};\n"
    DATA_IIFE = ("const DataStore = (function () {\n  function getComponents() { return []; }\n  function createBuild(b) { return b; }\n"
                 "  function exportBuilds() { return ''; }\n  return { getComponents, createBuild, exportBuilds };\n})();\nwindow.DataStore = DataStore;\n")
    APP_BROKEN = ("// app\nconst list = DataStore.getComponents();\nwindow.DataStore.addBuild({});\n"
                  "const out = DataStore.exportData ? DataStore.exportData() : '';\ndocument.getElementById('missing-id');\n")
    PAGE = '<html><body><input id="name" placeholder="Search"><script src="data.js"></script><script src="app.js"></script></body></html>\n'

    def _code_project(self, folder, files):
        root = Path(folder)
        for name, text in files.items():
            (root / name).write_text(text, encoding="utf-8")
        with contextlib.redirect_stdout(io.StringIO()):
            return orchestrator.code_gate(root, [], None)  # no allowed files: the contract check only

    def test_code_gate_finds_undefined_members_and_missing_ids(self):
        for data in (self.DATA_LITERAL, self.DATA_IIFE):
            with tempfile.TemporaryDirectory(dir=Path(__file__).parent) as folder:
                defects, line = self._code_project(folder, {"data.js": data, "app.js": self.APP_BROKEN, "index.html": self.PAGE})
                evidence = "\n".join(d["evidence"] for d in defects)
                self.assertIn("app.js:3 calls DataStore.addBuild, which data.js's DataStore does not define (it has: createBuild", evidence)
                self.assertIn("DataStore.exportData", evidence)
                self.assertIn('element id "missing-id"', evidence)
                self.assertEqual(len(defects), 3, evidence)
                self.assertIn("CODE CHECK: FAILED (3 issue(s))", line)

    def test_code_gate_flags_script_no_page_loads(self):
        page = '<html><body><ul id="task-list"></ul></body></html>\n'
        code = "document.getElementById('task-list');\n"
        with tempfile.TemporaryDirectory(dir=Path(__file__).parent) as folder:
            defects, _ = self._code_project(folder, {"index.html": page, "script.js": code, "tests.js": "x();\n"})
            self.assertEqual([(d["file"], d["defect"]) for d in defects], [("script.js", "Script is never loaded")])
        with tempfile.TemporaryDirectory(dir=Path(__file__).parent) as folder:  # loaded by the page, or imported by a loaded script
            page = page.replace("</body>", '<script type="module" src="script.js"></script></body>')
            files = {"index.html": page, "script.js": "import { a } from './util.js';\n" + code, "util.js": "export const a = 1;\n"}
            self.assertEqual(self._code_project(folder, files)[0], [])

    def test_validate_task_accepts_one_sentence_validation(self):
        task = dict(self._task(), validation="A task appears once per click.")
        with tempfile.TemporaryDirectory(dir=Path(__file__).parent) as folder:
            orchestrator.validate_task(task, Path(folder))
        self.assertEqual(task["validation"], ["A task appears once per click."])
        self.assertIn("## Validation\n- A task appears once per click.\n", orchestrator.task_markdown(task))

    def test_code_gate_clean_project_and_open_namespace_pass(self):
        clean = ("const list = DataStore.getComponents();\nDataStore.createBuild({});\nconst out = DataStore.exportBuilds();\n"
                 "document.getElementById('name'); document.querySelector('#made');\nconst el = document.createElement('p'); el.id = 'made';\n")
        with tempfile.TemporaryDirectory(dir=Path(__file__).parent) as folder:
            self.assertEqual(self._code_project(folder, {"data.js": self.DATA_LITERAL, "app.js": clean, "index.html": self.PAGE}),
                             ([], "CODE CHECK: passed"))
        assigned = "window.DataStore = Object.assign({}, base, { getComponents() {} });\n"
        with tempfile.TemporaryDirectory(dir=Path(__file__).parent) as folder:
            self.assertEqual(self._code_project(folder, {"data.js": assigned, "app.js": "DataStore.addBuild({});\n"})[0], [])
        with tempfile.TemporaryDirectory(dir=Path(__file__).parent) as folder:
            self.assertEqual(self._code_project(folder, {"a.py": "x = 1\n"}), ([], "CODE CHECK: skipped (no JavaScript)"))
            self.assertEqual(orchestrator.code_gate(Path(folder), [], None, {"code_check": False}), ([], ""))

    def test_code_gate_flags_only_added_stubs_and_guards(self):
        with tempfile.TemporaryDirectory(dir=Path(__file__).parent) as folder:
            root = Path(folder)
            old_app = "// TODO: old note\nconst a = 1;\n"
            files = {"data.js": "const DataStore = { load() {} };\n", "app.js": old_app, "index.html": self.PAGE}
            for name, text in files.items():
                (root / name).write_text(text, encoding="utf-8")
            before = orchestrator.snapshot(root, list(files))
            (root / "app.js").write_text(old_app + "// Comparison table logic would go here\nassertTrue(true, \"placeholder passes\");\n"
                                         "if (DataStore.fn) { run(); }\n", encoding="utf-8")
            (root / "index.html").write_text(self.PAGE + '<input placeholder="Find">\n', encoding="utf-8")
            with contextlib.redirect_stdout(io.StringIO()):
                defects, _ = orchestrator.code_gate(root, list(files), before)
            stubs = sorted({d["evidence"].split(" ", 1)[0] for d in defects if d["defect"].startswith("Stub")})
            self.assertEqual(stubs, ["app.js:3", "app.js:4", "app.js:5"])
            self.assertFalse(any("index.html" in d["evidence"] or "app.js:1" in d["evidence"] for d in defects))

    def _code_loop(self, app_before):
        """One task on data.js (allowed) while app.js (not allowed) calls the namespace; returns (roles, reviewer inputs, review)."""
        inputs = []

        def agent(config, role, system, user, *rest):
            inputs.append((role, user))
            if role == "reviewer":
                return '{"verdict":"PASS"}'
            (root / "data.js").write_text("window.DataStore = { createBuild() {} };\n", encoding="utf-8")
            return "PASSED"

        with tempfile.TemporaryDirectory(dir=Path(__file__).parent) as folder:
            root = self.make_project(folder)
            (root / "TASK.md").write_text("task\n", encoding="utf-8")
            (root / "data.js").write_text("window.DataStore = { createBuild() {}, addBuild() {} };\n", encoding="utf-8")
            (root / "app.js").write_text(app_before, encoding="utf-8")
            task = dict(self.TASK, allowed_files=["data.js"])
            before = orchestrator.snapshot(root, ["data.js"])
            with patch.object(orchestrator, "run_agent", side_effect=agent), patch.object(orchestrator, "emit"), \
                    contextlib.redirect_stdout(io.StringIO()):
                try:
                    orchestrator.run_review_loop({}, {"implementer": "", "reviewer": ""}, task, before, 0, "", "implementing", 1, root, False)
                except orchestrator.WorkflowError:
                    pass
            review = json.loads((root / "REVIEW.md").read_text(encoding="utf-8"))
        return [r for r, _ in inputs], [u for r, u in inputs if r == "reviewer"], review

    def test_new_contract_issue_fails_round_without_reviewer(self):
        roles, _, review = self._code_loop("DataStore.addBuild();\n")  # the task removed addBuild that app.js calls
        self.assertEqual(roles, ["implementer", "implementer"])
        self.assertEqual(review["verdict"], "FAIL")
        self.assertIn("calls DataStore.addBuild", review["defects"][0]["evidence"])

    def test_preexisting_contract_issue_reaches_reviewer(self):
        roles, reviewer, review = self._code_loop("DataStore.exportData();\n")
        self.assertEqual(roles, ["implementer", "reviewer"])
        self.assertEqual(review["verdict"], "PASS")
        self.assertIn("CODE CHECK: passed\nPre-existing issues (not caused by this task):\n- app.js:1 calls DataStore.exportData", reviewer[0])

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
            orchestrator.validate_task(self._task(5, 10), root)
            with self.assertRaises(orchestrator.WorkflowError):
                orchestrator.validate_task(self._task(6, 1), root)
            with self.assertRaises(orchestrator.WorkflowError):
                orchestrator.validate_task(self._task(1, 11), root)

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
        self.assertEqual(orchestrator.task_limits({}), (5, 10))
        self.assertEqual(orchestrator.task_limits({"task_limits": {"max_required_work": 5}}), (5, 5))
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

    def task_events(self, root, **kwargs):
        """Task-span events of one run_main, in order; (events, exception or None)."""
        events = []
        wanted = ("task_start", "task", "resume", "verdict", "task_end")
        with patch.object(orchestrator, "emit", lambda e, **d: events.append((e, d.get("status") or d.get("verdict")))):
            try:
                self.run_main(root, **kwargs)
            except orchestrator.WorkflowError as exc:
                return [e for e in events if e[0] in wanted], exc
        return [e for e in events if e[0] in wanted], None

    def test_task_span_events_on_pass_blocked_resume_and_complete(self):
        with tempfile.TemporaryDirectory(dir=Path(__file__).parent) as folder:
            root = self.make_project(folder)
            events, error = self.task_events(root, reviews=("FAIL", "PASS"))
            self.assertIsNone(error)
            self.assertEqual(events, [("task_start", None), ("task", None), ("verdict", "FAIL"), ("verdict", "PASS"),
                                      ("task_end", "passed")])
            (root / "config.json").write_text('{"max_review_rounds": 0}', encoding="utf-8")
            events, error = self.task_events(root, reviews=("FAIL",))
            self.assertIsNotNone(error)
            self.assertEqual(events, [("task_start", None), ("task", None), ("verdict", "FAIL"), ("task_end", "blocked")])
            orchestrator.save_checkpoint(root, self.TASK, {"app.js": None}, 0, "fix it", "implementing", 1)
            events, _ = self.task_events(root)
            self.assertEqual(events, [("task_start", None), ("resume", None), ("verdict", "PASS"), ("task_end", "passed")])
            events, _ = self.task_events(root, plan={"status": "COMPLETE"})
            self.assertEqual(events, [("task_start", None)])  # no task: the run's exit ends the span

    def test_complete_is_persisted_and_a_later_task_supersedes_it(self):
        with tempfile.TemporaryDirectory(dir=Path(__file__).parent) as folder:
            root = self.make_project(folder)
            (root / "STATE.md").write_text("# Verified state\n\n## Passed: WEB-012\n- x\n", encoding="utf-8")
            events = []
            with patch.object(orchestrator, "emit", lambda e, **d: events.append((e, d))):
                code, _, _ = self.run_main(root, plan={"status": "COMPLETE", "summary": "All\n## done"})
                self.run_main(root, plan={"status": "COMPLETE", "summary": "Still done"})
            self.assertEqual(code, 0)
            self.assertEqual([d for e, d in events if e == "complete"], [{"summary": "All ## done"}, {"summary": "Still done"}])
            state = (root / "STATE.md").read_text(encoding="utf-8")
            self.assertEqual(state.count(orchestrator.COMPLETE_HEADING), 1)  # a repeat replaces the marker
            self.assertTrue(state.rstrip().split("\n## ")[-1].startswith("Project complete\n- Time: "))
            self.assertTrue(state.rstrip().endswith("- Summary: Still done"))
            self.assertIn("## Passed: WEB-012", state)
            self.assertIn("Project complete.", (root / "HANDOFF.md").read_text(encoding="utf-8"))
            self.run_main(root)  # the Planner finds new work: the marker no longer applies
            state = (root / "STATE.md").read_text(encoding="utf-8")
            self.assertNotIn(orchestrator.COMPLETE_HEADING, state)
            self.assertIn("## Passed: T-9", state)
            self.assertNotIn("Project complete", (root / "HANDOFF.md").read_text(encoding="utf-8"))

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

    SCOPE_CONFIG = {"behaviour_check": False, "code_check": False}
    FAIL_INDEX = json.dumps({"verdict": "FAIL", "defects": [
        {"file": "index.html", "defect": "script.js is never loaded", "evidence": "acceptance step 2 failed: #out empty"}]})
    PASS = '{"verdict": "PASS", "evidence": ["ok"]}'

    def scope_run(self, root, task, implementer, reviews, decisions, rounds=2):
        """run_review_loop with faked agents. implementer(user, allowed) writes through execute_tool, so the
        task's real scope applies. Returns (Planner calls, events, stdout, WorkflowError or None)."""
        reviews, decisions, asked, events, out = iter(reviews), iter(decisions), [], [], io.StringIO()

        def agent(config, role, system, user, project_root, allowed, *rest):
            return next(reviews) if role == "reviewer" else implementer(user, list(allowed))

        def ask(config, role, system, user, *rest, **kwargs):
            asked.append((role, system, user))
            return next(decisions)

        before = orchestrator.snapshot(root, task["allowed_files"])
        (root / "TASK.md").write_text(orchestrator.task_markdown(task), encoding="utf-8")
        with patch.object(orchestrator, "run_agent", side_effect=agent), patch.object(orchestrator, "ask_once", side_effect=ask), \
                patch.object(orchestrator, "emit", lambda e, **d: events.append((e, d))), contextlib.redirect_stdout(out):
            try:
                orchestrator.run_review_loop(self.SCOPE_CONFIG, {"implementer": "", "reviewer": ""}, task, before, 0,
                                             "None; this is the first attempt.", "implementing", rounds, root, False)
                error = None
            except orchestrator.WorkflowError as exc:
                error = exc
        return asked, events, out.getvalue(), error

    @staticmethod
    def write(root, allowed, name, content):
        try:
            return orchestrator.execute_tool("write_file", {"path": name, "content": content}, root, allowed, {}, True)
        except orchestrator.WorkflowError as exc:
            return f"ERROR: {exc}"

    def test_needs_files_request_approved_widens_scope_for_the_next_round(self):
        with tempfile.TemporaryDirectory(dir=Path(__file__).parent) as folder:
            root = self.make_project(folder)
            (root / "index.html").write_text("<p id=out></p>\n", encoding="utf-8")
            task = dict(self.TASK, id="P1-T1", allowed_files=["script.js"])
            seen = []

            def implementer(user, allowed):
                seen.append((user, allowed, self.write(root, allowed, "index.html", "<script src=script.js></script>\n")))
                self.write(root, allowed, "script.js", "out.textContent = 'hi';\n")
                return "P1-T1 BLOCKED\n- **`needs_files: index.html` — the page never loads script.js**" if len(seen) == 1 else "P1-T1 PASSED"

            asked, events, out, error = self.scope_run(
                root, task, implementer, [self.FAIL_INDEX, self.PASS],
                ['{"approve": true, "files": ["index.html"], "reason": "the script tag is missing"}'])
            self.assertIsNone(error)
            self.assertEqual(len(asked), 1)
            role, system, user = asked[0]
            self.assertEqual(role, "planner")
            self.assertIn('"approve"', system)
            for expected in ("REQUESTED FILES\nindex.html", "the page never loads script.js", "--- index.html ---\n<p id=out></p>", "P1-T1"):
                self.assertIn(expected, user)
            self.assertTrue(seen[0][2].startswith("ERROR"))  # round 1: outside the scope
            self.assertEqual(seen[1][1], ["script.js", "index.html"])
            self.assertEqual(seen[1][2], "OK")  # round 2 may write it
            self.assertIn("SCOPE WIDENED\nindex.html added", seen[1][0])
            self.assertIn("--- index.html ---", seen[1][0])  # CURRENT ALLOWED FILES shows it
            self.assertIn("[scope] P1-T1 widened: index.html (the script tag is missing)", out)
            self.assertIn(("scope", {"id": "P1-T1", "files": ["index.html"], "reason": "the script tag is missing"}), events)
            self.assertEqual(task["allowed_files"], ["script.js", "index.html"])
            self.assertIn("- index.html", (root / "TASK.md").read_text(encoding="utf-8"))
            self.assertIn("## Passed: P1-T1", (root / "STATE.md").read_text(encoding="utf-8"))

    def test_widened_file_diff_starts_from_its_pre_widening_content(self):
        with tempfile.TemporaryDirectory(dir=Path(__file__).parent) as folder:
            root = self.make_project(folder)
            (root / "index.html").write_text("old page\n", encoding="utf-8")
            task = dict(self.TASK, allowed_files=["a.txt"])
            inputs = []

            def agent(config, role, system, user, project_root, allowed, *rest):
                if role == "reviewer":
                    inputs.append(user)
                    return self.FAIL_INDEX if len(inputs) == 1 else self.PASS
                self.write(root, allowed, "index.html", "new page\n")
                return "NEEDS_FILES: index.html — missing tag"

            before = orchestrator.snapshot(root, ["a.txt"])
            (root / "TASK.md").write_text("task\n", encoding="utf-8")
            with patch.object(orchestrator, "run_agent", side_effect=agent), \
                    patch.object(orchestrator, "ask_once", return_value='{"approve": true, "files": ["index.html"], "reason": "r"}'), \
                    patch.object(orchestrator, "emit"), contextlib.redirect_stdout(io.StringIO()):
                orchestrator.run_review_loop(self.SCOPE_CONFIG, {"implementer": "", "reviewer": ""}, task, before, 0, "", "implementing", 2, root, False)
            self.assertEqual(before["index.html"], "old page\n")
            self.assertNotIn("b/index.html", inputs[0])
            self.assertIn("-old page\n+new page", inputs[1])

    def test_rejected_scope_request_keeps_scope_and_records_the_reason(self):
        with tempfile.TemporaryDirectory(dir=Path(__file__).parent) as folder:
            root = self.make_project(folder)
            task = dict(self.TASK, allowed_files=["script.js"])
            seen = []

            def implementer(user, allowed):
                seen.append((user, allowed, self.write(root, allowed, "index.html", "x\n")))
                return "BLOCKED\nNEEDS_FILES: index.html - the tag is missing"

            asked, events, out, error = self.scope_run(
                root, task, implementer, [self.FAIL_INDEX] * 2,
                ['{"approve": false, "files": [], "reason": "a later task owns index.html"}'], rounds=1)
            self.assertRegex(str(error), "failed after 2 reviews")
            self.assertEqual(len(asked), 1)  # the second round's request is not decided again (no round left)
            self.assertEqual([s[1] for s in seen], [["script.js"], ["script.js"]])
            self.assertTrue(seen[1][2].startswith("ERROR"))
            self.assertIn("SCOPE REQUEST REJECTED\nThe Planner did not add index.html: a later task owns index.html", seen[1][0])
            self.assertNotIn("scope", [e for e, _ in events])
            self.assertEqual(task["allowed_files"], ["script.js"])
            issues = (root / "ISSUES.md").read_text(encoding="utf-8")
            self.assertIn("SCOPE REQUEST REJECTED (index.html): a later task owns index.html", issues)

    def test_same_outside_file_in_two_consecutive_rounds_raises_a_scope_request(self):
        with tempfile.TemporaryDirectory(dir=Path(__file__).parent) as folder:
            root = self.make_project(folder)
            task = dict(self.TASK, allowed_files=["script.js"])
            other = json.dumps({"verdict": "FAIL", "defects": [{"file": "script.js", "defect": "bug", "evidence": "e"}]})
            seen = []

            def implementer(user, allowed):
                seen.append(allowed)
                return "PASSED"  # never asks for files itself

            asked, _, out, error = self.scope_run(
                root, task, implementer, [self.FAIL_INDEX, other, self.FAIL_INDEX, "./index.html:3".join(self.FAIL_INDEX.split("index.html")), self.PASS],
                ['{"approve": true, "files": ["index.html"], "reason": "needs the tag"}'], rounds=4)
            self.assertIsNone(error)
            self.assertEqual(len(asked), 1)  # rounds 1 and 3 are not consecutive; 3 and 4 are
            self.assertEqual(seen, [["script.js"]] * 4 + [["script.js", "index.html"]])
            self.assertIn("two consecutive rounds", asked[0][2])
            self.assertIn("index.html: script.js is never loaded: acceptance step 2 failed", asked[0][2])
            self.assertIn("--- index.html --- (does not exist)", asked[0][2])

    def test_scope_request_never_names_secrets_or_dotfiles_and_server_failure_is_no_decision(self):
        with tempfile.TemporaryDirectory(dir=Path(__file__).parent) as folder:
            root = self.make_project(folder)
            for raw in (".env", "api_key.txt", ".git/hooks/pre-commit", "sub/.secret", "CONTRACT.md"):
                self.assertIsNone(orchestrator.scope_path(raw, root), raw)
                with self.assertRaises(orchestrator.WorkflowError):
                    orchestrator.path_is_allowed(raw, ["*", "**/*", raw], root)
            task = dict(self.TASK, allowed_files=["a.txt"])
            with patch.object(orchestrator, "ask_once", side_effect=orchestrator.WorkflowError("Cannot reach http://x")), \
                    contextlib.redirect_stdout(io.StringIO()), self.assertRaisesRegex(orchestrator.WorkflowError, "Cannot reach"):
                orchestrator.widen_scope({}, task, ["a.txt"], {}, ["index.html"], "why", root, False)
            self.assertNotIn("scope", task)  # a resume may still ask
            full = dict(self.TASK, allowed_files=["a.txt", "b.txt", "c.txt", "d.txt", "e.txt", "f.txt", "g.txt"])
            with patch.object(orchestrator, "ask_once") as ask, contextlib.redirect_stdout(io.StringIO()):
                note = orchestrator.widen_scope({}, full, list(full["allowed_files"]), {}, ["index.html"], "why", root, False)
            self.assertEqual((ask.call_count, "scope" in full), (0, False))
            self.assertIn("most files a task may have", note)

    def test_needs_files_line_without_a_dash_and_free_form_defect_files(self):
        with tempfile.TemporaryDirectory(dir=Path(__file__).parent) as folder:
            root = self.make_project(folder)
            for line, files, reason in [
                ("NEEDS_FILES: index.html because the tag is missing", ["index.html"], "because the tag is missing"),
                ("NEEDS_FILES: index.html: the tag is missing", ["index.html"], "the tag is missing"),
                ("NEEDS_FILES: index.html, app.js — wiring", ["index.html", "app.js"], "wiring"),
            ]:
                self.assertEqual(orchestrator.scope_request(f"T PASSED\n{line}", "", "", ["script.js"], root), (files, reason), line)
            review = json.dumps({"verdict": "FAIL", "defects": [{"file": "index.html (line 3)", "defect": "d", "evidence": "e"}]})
            self.assertEqual(list(orchestrator.outside_defects(review, ["script.js"], root)), ["index.html"])

    def test_scope_request_refusals(self):
        with tempfile.TemporaryDirectory(dir=Path(__file__).parent) as folder:
            root = self.make_project(folder)

            def decide(task, requested, reply=None):
                allowed, before = list(task["allowed_files"]), {}
                with patch.object(orchestrator, "ask_once", return_value=reply) as ask, patch.object(orchestrator, "emit"), \
                        contextlib.redirect_stdout(io.StringIO()):
                    note = orchestrator.widen_scope({}, task, allowed, before, requested, "why", root, False)
                return note, ask.call_count, allowed

            # Protected, escaping, glob and already-allowed names never reach the Planner and use up nothing.
            task = dict(self.TASK, allowed_files=["a.txt"])
            note, calls, allowed = decide(task, ["STATE.md", "../x.js", "*.js", "a.txt"])
            self.assertEqual((calls, allowed, "scope" in task), (0, ["a.txt"], False))
            self.assertIn("SCOPE REQUEST REFUSED", note)
            # Over the limit: five files allowed (max 5), so at most two more.
            task = dict(self.TASK, allowed_files=["a.txt", "b.txt", "c.txt", "d.txt", "e.txt"])
            note, calls, allowed = decide(task, ["f.txt", "g.txt", "h.txt"], '{"approve": true, "files": ["f.txt", "g.txt", "h.txt"], "reason": "all"}')
            self.assertEqual((calls, allowed), (1, ["a.txt", "b.txt", "c.txt", "d.txt", "e.txt"]))
            self.assertIn("only up to 2", note)
            self.assertFalse(task["scope"]["approved"])
            # Approving a file that was not requested is refused too.
            task = dict(self.TASK, allowed_files=["a.txt"])
            note, _, allowed = decide(task, ["d.txt"], '{"approve": true, "files": ["d.txt", "z.txt"], "reason": "r"}')
            self.assertEqual(allowed, ["a.txt"])
            # One request per task: after an approval a second request is refused without asking.
            task = dict(self.TASK, allowed_files=["a.txt"])
            decide(task, ["d.txt"], '{"approve": true, "files": ["d.txt"], "reason": "r"}')
            self.assertEqual(task["allowed_files"], ["a.txt", "d.txt"])
            note, calls, allowed = decide(task, ["e.txt"], '{"approve": true, "files": ["e.txt"], "reason": "r"}')
            self.assertEqual((calls, allowed), (0, ["a.txt", "d.txt"]))
            self.assertIn("already had its one scope request", note)

    def test_resume_after_widening_keeps_the_widened_scope(self):
        with tempfile.TemporaryDirectory(dir=Path(__file__).parent) as folder:
            root = self.make_project(folder)
            (root / "config.json").write_text(json.dumps(self.SCOPE_CONFIG), encoding="utf-8")
            (root / "index.html").write_text("old\n", encoding="utf-8")
            task = dict(self.TASK, allowed_files=["a.txt", "b.txt", "c.txt"])  # at the limit: the widened task has 4

            def implementer(user, allowed):
                if "SCOPE WIDENED" in user:
                    raise orchestrator.WorkflowError("killed")
                return "NEEDS_FILES: index.html — tag"

            _, _, _, error = self.scope_run(root, task, implementer, [self.FAIL_INDEX],
                                            ['{"approve": true, "files": ["index.html"], "reason": "r"}'])
            self.assertEqual(str(error), "killed")
            checkpoint = orchestrator.load_checkpoint(root, (3, 7), 3)
            self.assertIsNotNone(checkpoint)
            self.assertEqual(checkpoint["task"]["allowed_files"], ["a.txt", "b.txt", "c.txt", "index.html"])
            self.assertEqual((checkpoint["round"], checkpoint["before"]["index.html"]), (1, "old\n"))
            self.assertIn("SCOPE WIDENED", checkpoint["reviewer_feedback"])
            seen = []

            def agent(config, role, system, user, project_root, allowed, *rest):
                seen.append(list(allowed))
                return "did it"

            code, plan, _ = self.run_main(root, agent=agent)
            self.assertEqual(code, 0)
            plan.assert_not_called()
            self.assertEqual(seen[0], ["a.txt", "b.txt", "c.txt", "index.html"])
            self.assertIn("- index.html", (root / "TASK.md").read_text(encoding="utf-8"))

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
        bad_task = dict(self.TASK, allowed_files=["a", "b", "c", "d", "e", "f"])
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


class ChatTests(unittest.TestCase):
    """--chat: single worker or Planner -> Implementer -> Reviewer, in a folder holding only config.json."""

    def chat(self, request, replies=None, workers=None):
        replies = {k: list(v) for k, v in (replies or {}).items()}
        roles = {r: {"base_url": "http://x", "expected_model_contains": f"{r}-model", "sampling": {"temperature": i}}
                 for i, r in enumerate(("planner", "implementer", "reviewer"))}
        config = {"roles": roles, **({"custom_workers": workers} if workers else {})}
        log, events, unloaded = [], [], []

        class Fake:
            def __init__(self, settings):
                self.settings, self.role, self.title = settings, "", ""

            def ensure_model(self, role, interactive):
                return "m"

            def complete(self, model, messages, sampling, tools=None):
                log.append({"model": self.settings["expected_model_contains"], "system": messages[0]["content"],
                            "input": "\n".join(m["content"] for m in messages[1:]), "sampling": sampling, "tools": tools})
                return {"role": "assistant", "content": replies[self.role].pop(0) if replies.get(self.role) else f"{self.role} text"}

        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            (root / "config.json").write_text(json.dumps(config), encoding="utf-8")
            req = Path(folder) / "request.json"
            req.write_text(json.dumps(request), encoding="utf-8")
            argv = ["o", "--project", folder, "--chat", str(req), "--events", "--non-interactive"]
            with patch.object(orchestrator, "ApiClient", Fake), patch.object(orchestrator, "emit", lambda e, **d: events.append((e, d))), \
                    patch.object(orchestrator, "unload_models", lambda: unloaded.append(1)), patch.object(orchestrator.sys, "argv", argv):
                code = orchestrator.main()
            files = sorted(p.name for p in root.iterdir())
        return code, log, events, unloaded, files

    MSGS = [{"role": "user", "content": "hi"}, {"role": "assistant", "content": "hello"}, {"role": "user", "content": "explain X"}]

    def test_single_builtin_uses_own_settings_and_streams_reply(self):
        code, log, events, unloaded, files = self.chat({"mode": "single", "worker": "implementer", "messages": self.MSGS},
                                                       {"implementer": ["<think>hmm</think>the answer"]})
        self.assertEqual(code, 0)
        self.assertEqual(len(log), 1)
        self.assertEqual((log[0]["model"], log[0]["sampling"], log[0]["tools"]), ("implementer-model", {"temperature": 1}, None))
        self.assertIn((Path(orchestrator.ROOT) / "prompts" / "chat.txt").read_text(encoding="utf-8").strip(), log[0]["system"])
        self.assertIn("Implementer worker", log[0]["system"])
        self.assertIn("explain X", log[0]["input"])
        self.assertEqual([e for e in events if e[0].startswith("chat_")],
                         [("chat_stage", {"role": "implementer", "stage": "answer", "title": "implementer"}),
                          ("chat_reply", {"role": "implementer", "text": "the answer"})])
        # G3: no workflow files, models stay loaded
        self.assertEqual(files, ["config.json", "request.json"])
        self.assertEqual(unloaded, [])

    def test_single_custom_worker_and_unknown_worker(self):
        worker = {"id": "spec1", "title": "Tester", "kind": "specialist", "description": "d", "system_prompt": "Be a tester.",
                  "base_url": "http://x", "expected_model_contains": "spec1-model"}
        code, log, events, *_ = self.chat({"mode": "single", "worker": "spec1", "messages": self.MSGS}, workers=[worker])
        self.assertEqual((code, log[0]["model"]), (0, "spec1-model"))
        self.assertIn("=== YOUR ROLE: Tester ===\nBe a tester.", log[0]["system"])
        with self.assertRaisesRegex(orchestrator.WorkflowError, "Unknown chat worker"):
            self.chat({"mode": "single", "worker": "nobody", "messages": self.MSGS})

    def test_unknown_worker_prints_error_and_exits_1(self):
        import subprocess
        import sys
        with tempfile.TemporaryDirectory() as folder:
            (Path(folder) / "config.json").write_text('{"roles": {}}', encoding="utf-8")
            (Path(folder) / "r.json").write_text(json.dumps({"mode": "single", "worker": "nobody", "messages": self.MSGS}), encoding="utf-8")
            done = subprocess.run([sys.executable, str(Path(orchestrator.__file__)), "--project", folder, "--chat", str(Path(folder) / "r.json"),
                                   "--events", "--non-interactive"], capture_output=True, text=True)
        self.assertEqual(done.returncode, 1)
        self.assertIn("ERROR: Unknown chat worker", done.stderr)

    def _bad_chat(self, config, request):
        import subprocess
        import sys
        with tempfile.TemporaryDirectory() as folder:
            (Path(folder) / "config.json").write_text(json.dumps(config), encoding="utf-8")
            (Path(folder) / "r.json").write_text(json.dumps(request), encoding="utf-8")
            done = subprocess.run([sys.executable, str(Path(orchestrator.__file__)), "--project", folder, "--chat", str(Path(folder) / "r.json"),
                                   "--events", "--non-interactive"], capture_output=True, text=True)
        self.assertEqual(done.returncode, 1)
        self.assertIn("ERROR:", done.stderr)
        self.assertNotIn("Traceback", done.stderr)

    def test_builtin_role_missing_from_config_is_a_clean_error(self):
        self._bad_chat({"roles": {}}, {"mode": "single", "worker": "planner", "messages": self.MSGS})

    def test_non_object_request_is_a_clean_error(self):
        self._bad_chat({"roles": {}}, [1, 2])

    def test_multi_runs_three_stages_in_order(self):
        code, log, events, unloaded, files = self.chat(
            {"mode": "multi", "worker": "ignored", "messages": self.MSGS},
            {"planner": ["<think>x</think>PLAN-A"], "implementer": ["DRAFT-B"], "reviewer": ["FINAL-C"]})
        self.assertEqual(code, 0)
        self.assertEqual([(l["model"], l["sampling"]["temperature"]) for l in log],
                         [("planner-model", 0), ("implementer-model", 1), ("reviewer-model", 2)])
        self.assertNotIn("PLAN-A", log[0]["input"])
        self.assertIn("PLAN-A", log[1]["input"])
        self.assertNotIn("x</think>", log[1]["input"] + log[2]["input"])
        self.assertTrue("PLAN-A" in log[2]["input"] and "DRAFT-B" in log[2]["input"] and "explain X" in log[2]["input"])
        self.assertEqual([(d["stage"], d["role"]) for e, d in events if e == "chat_stage"],
                         [("plan", "planner"), ("draft", "implementer"), ("review", "reviewer")])
        self.assertEqual([d for e, d in events if e == "chat_reply"], [{"role": "reviewer", "text": "FINAL-C"}])
        self.assertEqual((files, unloaded), (["config.json", "request.json"], []))

    def test_multi_falls_back_to_draft_when_reviewer_is_empty(self):
        out = io.StringIO()
        out.reconfigure = lambda **kw: None
        with patch.object(orchestrator.sys, "stdout", out):
            _, _, events, *_ = self.chat({"mode": "multi", "messages": self.MSGS}, {"reviewer": ["<think>only thoughts"]})
        self.assertEqual([d for e, d in events if e == "chat_reply"], [{"role": "implementer", "text": "implementer text"}])
        self.assertIn("Reviewer returned nothing usable", out.getvalue())


BROWSER = orchestrator.find_browser()


class BehaviourGateTests(unittest.TestCase):
    def gate(self, files, config=None, steps=None, allowed=None):
        with tempfile.TemporaryDirectory(dir=Path(__file__).parent) as folder:
            for name, text in files.items():
                (Path(folder) / name).write_text(text, encoding="utf-8")
            with contextlib.redirect_stdout(io.StringIO()):
                return orchestrator.behaviour_gate(Path(folder), config or {}, steps, allowed)

    def test_not_a_web_project_or_disabled_is_silent(self):
        self.assertEqual(self.gate({"app.py": "x = 1\n"}), ([], ""))
        self.assertEqual(self.gate({"index.html": "<p>x</p>"}, {"behaviour_check": False}), ([], ""))

    def test_no_browser_skips_with_note(self):
        with patch.object(orchestrator, "find_browser", return_value=None):
            defects, block = self.gate({"index.html": "<p>x</p>"})
        self.assertEqual(defects, [])
        self.assertIn("BEHAVIOUR CHECK: skipped (no Chromium browser found", block)

    def test_missing_report_is_a_gate_error_not_a_pass(self):
        with patch.object(orchestrator, "find_browser", return_value="browser"), \
                patch.object(orchestrator, "load_page", side_effect=orchestrator.WorkflowError("no report came back")):
            defects, block = self.gate({"index.html": "<p>x</p>"})
        self.assertEqual(defects[0]["evidence"], "no report came back")
        self.assertIn("FAILED", block)

    @unittest.skipUnless(BROWSER, "no Chromium browser installed")
    def test_real_browser_reports_error_thrown_on_load(self):
        defects, block = self.gate({
            "index.html": "<!doctype html><html><head><title>t</title></head><body><p>Hi</p><script src='app.js'></script></body></html>",
            "app.js": "const components = {};\nfor (const c of components) {}\n",
        })
        self.assertIn("components is not iterable (app.js:2)", defects[0]["evidence"])
        self.assertIn("BEHAVIOUR CHECK: FAILED", block)

    @unittest.skipUnless(BROWSER, "no Chromium browser installed")
    def test_real_browser_clean_page_passes_with_visible_text(self):
        defects, block = self.gate({
            "index.html": "<html><body><h1>Tracker</h1><script src='app.js'></script></body></html>",
            "app.js": "document.body.insertAdjacentHTML('beforeend', '<p>Added by script</p>');\n",
        })
        self.assertEqual(defects, [])
        self.assertIn("BEHAVIOUR CHECK: passed", block)
        self.assertIn("Tracker\n", block)
        self.assertIn("Added by script", block)

    @unittest.skipUnless(BROWSER, "no Chromium browser installed")
    def test_real_browser_reports_missing_script_and_console_error(self):
        defects, _ = self.gate({"index.html": "<p>x</p><script src='app.js'></script><script>console.error('boom')</script>"})
        evidence = [d["evidence"] for d in defects]
        self.assertIn("Failed to load app.js (404 not found): create the file; never remove the reference (index.html)", evidence)
        self.assertTrue(any(e == "boom (console.error)" for e in evidence), evidence)

    def test_real_browser_missing_script_outside_allowed_files_is_noted_not_failed(self):
        files = {"index.html": "<p>x</p><link rel='stylesheet' href='style.css'><script src='app.js'></script>"}
        defects, block = self.gate(files, steps=[], allowed=["index.html"])
        self.assertEqual(defects, [])
        self.assertIn("Not created yet (outside this task's allowed files; keep the reference, a later task creates it): ", block)
        self.assertIn("app.js", block.split("Not created yet", 1)[1])
        defects, _ = self.gate(files, steps=[], allowed=["index.html", "app.js"])  # the task's own file must exist
        self.assertEqual([d["evidence"].split(":")[0] for d in defects], ["Failed to load app.js (404 not found)"])

    BUILD_PAGE = (
        "<!doctype html><html><head><title>Builds</title></head><body>"
        "<form id='build-form'><input id='build-name'><select id='category'><option value='cpu'>CPU</option>"
        "<option value='gpu'>GPU</option></select><label><input type='checkbox' id='fav'> Favourite</label>"
        "<button type='button' id='save-build'>Save</button></form><div id='secret' hidden>s</div>"
        "<p id='status'></p><p id='view'></p><ul id='build-list'></ul>"
        "<script src='data.js'></script><script src='app.js'></script></body></html>"
    )
    BUILD_APP = (
        "addEventListener('hashchange', function () { document.getElementById('view').textContent = 'view ' + location.hash; });\n"
        "document.getElementById('save-build').addEventListener('click', function () {\n"
        "  try {\n"
        "    var all = window.DataStore.addBuild({name: document.getElementById('build-name').value,\n"
        "      category: document.getElementById('category').value, fav: document.getElementById('fav').checked});\n"
        "    document.getElementById('build-list').innerHTML = all.map(function (b) {\n"
        "      return '<li>' + b.name + ' (' + b.category + (b.fav ? ', fav' : '') + ')</li>'; }).join('');\n"
        "  } catch (e) { document.getElementById('status').textContent = 'Error saving build: ' + e.message; }\n"
        "});\n"
    )
    WORKING_STORE = ("window.DataStore = {addBuild: function (b) { var all = JSON.parse(localStorage.getItem('builds') || '[]');"
                     " all.push(b); localStorage.setItem('builds', JSON.stringify(all)); return all; }};\n")
    BUILD_STEPS = [
        {"do": "fill", "selector": "#build-name", "value": "Gaming PC"},
        {"do": "select", "selector": "#category", "value": "GPU"},
        {"do": "check", "selector": "#fav", "value": True},
        {"do": "click", "selector": "#save-build"},
        {"expect": "text", "selector": "#build-list", "contains": "Gaming PC (gpu, fav)"},
        {"expect": "storage", "key": "builds", "contains": "Gaming PC", "json_length_min": 1},
        {"expect": "count", "selector": "#build-list li", "min": 1, "max": 1},
        {"do": "goto", "hash": "#builds"},
        {"do": "wait", "ms": 100},
        {"expect": "text", "selector": "#view", "contains": "view #builds"},
        {"expect": "visible", "selector": "#build-form"},
        {"expect": "hidden", "selector": "#secret"},
    ]

    @unittest.skipUnless(BROWSER, "no Chromium browser installed")
    def test_real_browser_acceptance_steps_pass_when_save_works(self):
        files = {"index.html": self.BUILD_PAGE, "app.js": self.BUILD_APP, "data.js": self.WORKING_STORE}
        defects, block = self.gate(files, steps=self.BUILD_STEPS)
        self.assertEqual(defects, [])
        self.assertIn("BEHAVIOUR CHECK: passed", block)
        self.assertIn("Acceptance steps (12/12 passed):\n✓ 1. {\"do\": \"fill\"", block)
        self.assertIn('✓ 12. {"expect": "hidden", "selector": "#secret"}', block)
        self.assertIn("Gaming PC (gpu, fav)", block.split("Visible text sample:", 1)[1])

    @unittest.skipUnless(BROWSER, "no Chromium browser installed")
    def test_real_browser_acceptance_step_catches_save_that_shows_an_error(self):
        files = {"index.html": self.BUILD_PAGE, "app.js": self.BUILD_APP, "data.js": "window.DataStore = {};\n"}
        defects, block = self.gate(files, steps=self.BUILD_STEPS)
        self.assertEqual(len(defects), 1, defects)  # the error is caught and shown in the page: no console error
        self.assertEqual(
            defects[0]["evidence"],
            'acceptance step 5 failed: {"expect": "text", "selector": "#build-list", "contains": "Gaming PC (gpu, fav)"}'
            " — expected text 'Gaming PC (gpu, fav)' in #build-list; got ''")
        self.assertIn("BEHAVIOUR CHECK: FAILED", block)
        self.assertIn('- 6. {"expect": "storage", "key": "builds", "contains": "Gaming PC", "json_length_min": 1} — not run', block)
        self.assertIn("— not run", block.splitlines()[-1])
        defects, _ = self.gate(files, steps=self.BUILD_STEPS[:4] + self.BUILD_STEPS[5:6])
        self.assertIn("— localStorage['builds'] missing", defects[0]["evidence"])

    @unittest.skipUnless(BROWSER, "no Chromium browser installed")
    def test_real_browser_missing_selector_and_page_error_fail_their_step(self):
        defects, block = self.gate({"index.html": "<p>x</p>"}, steps=[{"do": "click", "selector": "#nope"}, {"expect": "no_errors"}])
        self.assertEqual([d["evidence"] for d in defects], ['acceptance step 1 failed: {"do": "click", "selector": "#nope"} — selector #nope not found'])
        self.assertIn('- 2. {"expect": "no_errors"} — not run', block)
        page = "<button id='b' onclick=\"throw new Error('kaboom')\">b</button>"
        defects, _ = self.gate({"index.html": page}, steps=[{"do": "click", "selector": "#b"}, {"expect": "no_errors"}])
        evidence = [d["evidence"] for d in defects]
        self.assertTrue(any("kaboom" in e and "index.html" in e for e in evidence[:-1]), evidence)  # counted as a page error
        self.assertTrue(evidence[-1].startswith("acceptance step 1 failed:") and "page error: Uncaught Error: kaboom" in evidence[-1], evidence)


class ContractTests(unittest.TestCase):
    """CONTRACT.md: created once by the orchestrator, amended by contract_changes, shown to every agent, enforced."""

    CONTRACT = {"files": [{"path": "index.html", "purpose": "page", "loads": ["style.css", "script.js"]},
                          {"path": "script.js", "purpose": "logic"}],
                "ids": [{"id": "task-input", "element": "input", "file": "index.html", "purpose": "new task"}]}
    TASK = {"id": "T-1", "outcome": "Add tasks", "allowed_files": ["script.js"], "required_work": ["work"],
            "validation": ["check"], "stop_condition": "stop"}
    CONTRACT_PROMPT = orchestrator.load_text(orchestrator.ROOT / "prompts" / "contract.txt")

    def project(self, folder, config="{}", files=None):
        root = Path(folder)
        for name in orchestrator.WORKFLOW_FILES:
            (root / name).write_text(config if name == "config.json" else "Build a todo app\n" if name == "PROJECT.md" else "text\n", encoding="utf-8")
        for name, text in (files or {}).items():
            (root / name).write_text(text, encoding="utf-8")
        return root

    def run_orchestrator(self, root, plans):
        """_run with faked models: the contract prompt gets CONTRACT, the planner gets `plans` in order."""
        asked, agents, plans = [], [], iter(plans)

        def ask(config, role, system, user, interactive, thinking=True):
            asked.append(("contract" if system == self.CONTRACT_PROMPT else "plan", user))
            return json.dumps(self.CONTRACT if system == self.CONTRACT_PROMPT else next(plans))

        def agent(config, role, system, user, *rest):
            agents.append((role, user, (root / orchestrator.CONTRACT_FILE).is_file()))
            return "did it"
        argv = ["o", "--project", str(root), "--non-interactive"]
        with patch.object(orchestrator, "ask_once", side_effect=ask), patch.object(orchestrator, "run_agent", side_effect=agent), \
                patch.object(orchestrator, "extract_review_with_repair", side_effect=lambda *a: ({"verdict": "PASS"}, "ok")), \
                patch.object(orchestrator, "configure_project", side_effect=lambda c, *a: c), patch.object(orchestrator.sys, "argv", argv):
            self.assertEqual(orchestrator._run(), 0)
        return asked, agents

    def test_contract_created_once_before_the_first_task_and_shown_to_every_agent(self):
        plans = [{"status": "TASK", "task": self.TASK}, {"status": "TASK", "task": dict(self.TASK, id="T-2")}, {"status": "COMPLETE"}]
        with tempfile.TemporaryDirectory(dir=Path(__file__).parent) as folder:
            root = self.project(folder)
            asked, agents = self.run_orchestrator(root, plans)
            self.assertEqual([kind for kind, _ in asked], ["contract", "plan", "plan", "plan"])
            self.assertIn("PROJECT\nBuild a todo app", asked[0][1])
            self.assertEqual(orchestrator.load_contract(root), self.CONTRACT)
            text = (root / "CONTRACT.md").read_text(encoding="utf-8")
            self.assertTrue(text.startswith("# Project contract") and "```json\n{" in text, text)
            for kind, user in asked[1:]:
                self.assertIn('PROJECT CONTRACT\n{\n  "files"', user)
            self.assertEqual([role for role, *_ in agents], ["implementer", "reviewer"] * 2)
            for role, user, existed in agents:
                self.assertTrue(existed)
                self.assertIn('PROJECT CONTRACT\n{\n  "files"', user)
            with self.assertRaises(orchestrator.WorkflowError):  # models may not write it
                orchestrator.path_is_allowed("CONTRACT.md", ["*"], root)
            self.assertIn("CONTRACT.md.tmp", orchestrator.PROTECTED_FILES)

    def test_contract_false_changes_nothing(self):
        plans = [{"status": "TASK", "task": dict(self.TASK, contract_changes={"ids": [{"id": "x"}]})}, {"status": "COMPLETE"}]
        with tempfile.TemporaryDirectory(dir=Path(__file__).parent) as folder:
            root = self.project(folder, '{"contract": false}')
            asked, agents = self.run_orchestrator(root, plans)
            self.assertEqual([kind for kind, _ in asked], ["plan", "plan"])
            self.assertFalse((root / "CONTRACT.md").exists())
            self.assertFalse(any("PROJECT CONTRACT" in user for _, user in asked) or any("PROJECT CONTRACT" in user for _, user, _ in agents))
        with tempfile.TemporaryDirectory(dir=Path(__file__).parent) as folder:  # a stale CONTRACT.md is ignored by the code check
            root = self.project(folder, files={"index.html": "<p></p>", "script.js": "document.getElementById('ghost');\n"})
            orchestrator.write_contract(root, self.CONTRACT)
            with contextlib.redirect_stdout(io.StringIO()):
                defects, _ = orchestrator.code_gate(root, [], None, {"contract": False})
            self.assertFalse([d for d in defects if "contract" in d["defect"]], defects)

    def test_contract_changes_merge_into_contract_md_before_the_task(self):
        changes = {"files": [{"path": "script.js", "purpose": "all logic"}, {"path": "store.js", "purpose": "storage"}],
                   "ids": [{"id": "save-btn", "element": "button"}], "storage_keys": [{"key": "todo.tasks", "shape": "array"}]}
        task = dict(self.TASK, contract_changes=changes, acceptance=[{"do": "click", "selector": "#save-btn"}])
        with tempfile.TemporaryDirectory(dir=Path(__file__).parent) as folder:
            root = self.project(folder)
            orchestrator.write_contract(root, self.CONTRACT)
            with patch.object(orchestrator, "ask_once", return_value=json.dumps({"status": "TASK", "task": task})) as ask:
                orchestrator.request_plan({}, "SYS", "USER", root, False, with_contract=True)
            self.assertEqual(ask.call_count, 1)  # the contract existed: no creation call
            merged = orchestrator.load_contract(root)
            self.assertEqual([(f["path"], f["purpose"]) for f in merged["files"]],
                             [("index.html", "page"), ("script.js", "all logic"), ("store.js", "storage")])
            self.assertEqual([i["id"] for i in merged["ids"]], ["task-input", "save-btn"])
            self.assertEqual(merged["storage_keys"], [{"key": "todo.tasks", "shape": "array"}])
            bad = dict(self.TASK, contract_changes={"ids": [{"id": "a"}, {"id": "a"}]})
            with self.assertRaisesRegex(orchestrator.WorkflowError, "repeats a id: a"):
                orchestrator.validate_task(bad, root, contract=self.CONTRACT)
            url = dict(self.TASK, contract_changes={"files": [{"path": "index.html", "loads": ["https://cdn.x/a.js"]}]})
            with self.assertRaisesRegex(orchestrator.WorkflowError, "not URLs"):
                orchestrator.validate_task(url, root, contract=self.CONTRACT)

    def test_acceptance_selector_must_use_a_contract_id(self):
        def task(*selectors, **extra):
            return dict(self.TASK, acceptance=[{"expect": "visible", "selector": s} for s in selectors], **extra)
        with tempfile.TemporaryDirectory(dir=Path(__file__).parent) as folder:
            root = Path(folder)
            orchestrator.validate_task(task("#task-input", "#task-input li", ".item", "li"), root, contract=self.CONTRACT)
            orchestrator.validate_task(task("#new-id"), root, contract=None)  # no contract, no check
            orchestrator.validate_task(task("#new-id", contract_changes={"ids": [{"id": "new-id"}]}), root, contract=self.CONTRACT)
            with self.assertRaisesRegex(orchestrator.WorkflowError, "step 2 selects #nope, which is not a contract id"):
                orchestrator.validate_task(task("#task-input", "#nope"), root, contract=self.CONTRACT)
            orchestrator.write_contract(root, self.CONTRACT)
            replies = [json.dumps({"status": "TASK", "task": task(s)}) for s in ("#nope", "#task-input")]
            with patch.object(orchestrator, "ask_once", side_effect=replies) as ask, contextlib.redirect_stdout(io.StringIO()):
                plan = orchestrator.request_plan({}, "SYS", "USER", root, False, with_contract=True)
            self.assertEqual(plan["task"]["acceptance"][0]["selector"], "#task-input")
            self.assertIn("not a contract id", ask.call_args.args[3])

    PAGE = '<html><body><input id="task-input"><p id="ghost"></p><script src="script.js"></script></body></html>\n'
    SCRIPT = "document.getElementById('task-input');\n"

    def gate(self, root, before=None, allowed=()):
        with contextlib.redirect_stdout(io.StringIO()):
            return orchestrator.code_gate(root, list(allowed), before, {})

    def test_code_check_enforces_contract_wiring_and_ids(self):
        with tempfile.TemporaryDirectory(dir=Path(__file__).parent) as folder:
            root = self.project(folder, files={"index.html": self.PAGE, "script.js": self.SCRIPT})
            orchestrator.write_contract(root, self.CONTRACT)
            self.assertEqual(self.gate(root)[0], [])  # style.css does not exist yet: not required
            files = ["index.html", "script.js"]
            before = orchestrator.snapshot(root, files)
            (root / "index.html").write_text(self.PAGE.replace('<script src="script.js"></script>', ""), encoding="utf-8")
            (root / "script.js").write_text(self.SCRIPT + "document.querySelector('#ghost');\n", encoding="utf-8")
            defects, line = self.gate(root, before, files)
            contract = {(d["defect"], d["evidence"].split(";")[-1].strip()) for d in defects if "contract" in d["defect"]}
            self.assertEqual(contract, {
                ("Page does not load a file the contract wires to it", 'add <script src="script.js"></script> to index.html'),
                ("Element id not in the contract", "use the contract's id for this element (contract ids: task-input)"),
            })
            self.assertIn('script.js:2 looks up element id "ghost"', line)
            # the same issues already present before the task are reported, not failed
            defects, line = self.gate(root, orchestrator.snapshot(root, files), files)
            self.assertEqual(defects, [])
            self.assertIn("Pre-existing issues", line)
            self.assertIn("CONTRACT.md says it loads", line)

    def test_root_relative_load_counts_and_backticks_in_values_keep_the_contract(self):
        with tempfile.TemporaryDirectory(dir=Path(__file__).parent) as folder:
            page = self.PAGE.replace('src="script.js"', 'src="/script.js"')
            root = self.project(folder, files={"index.html": page, "script.js": self.SCRIPT})
            contract = dict(self.CONTRACT, notes=["use ```js``` blocks in docs"])
            orchestrator.write_contract(root, contract)
            self.assertEqual(orchestrator.load_contract(root)["notes"], contract["notes"])
            self.assertEqual(self.gate(root)[0], [])

    WIRED = {"files": [{"path": "index.html", "purpose": "Todo page", "loads": ["style.css", "script.js"]},
                       {"path": "script.js", "purpose": "todo logic"}, {"path": "main.py", "purpose": "server"},
                       {"path": ".hidden.js", "purpose": "dotfile"}],
             "ids": [{"id": "task-input", "element": "input", "file": "index.html"}, {"id": "add-btn", "element": "button"}]}

    def ensure(self, root, config=None, contract=None):
        out = io.StringIO()
        with patch.object(orchestrator, "ask_once", return_value=json.dumps(contract or self.WIRED)), contextlib.redirect_stdout(out):
            orchestrator.ensure_contract(config or {}, root, "PROJECT", False)
        return out.getvalue()

    def files(self, root):
        return {p.relative_to(root).as_posix(): p.read_bytes() for p in root.rglob("*") if p.is_file()}

    def test_scaffold_wires_the_contract_page_and_creates_its_files(self):
        with tempfile.TemporaryDirectory(dir=Path(__file__).parent) as folder:
            root = self.project(folder)
            out = self.ensure(root)
            self.assertIn("[scaffold] created index.html, script.js, style.css\n", out)
            self.assertFalse((root / "main.py").exists() or (root / ".hidden.js").exists())  # not web / dotfile
            page = (root / "index.html").read_text(encoding="utf-8")
            for part in ("<!DOCTYPE html>", '<html lang="en">', '<meta charset="utf-8">', 'name="viewport"', "<title>Todo page</title>",
                         '<link rel="stylesheet" href="style.css">', '<main>\n    <input id="task-input">\n    <button id="add-btn"></button>\n  </main>',
                         '<script src="script.js"></script>\n</body>'):
                self.assertIn(part, page)
            self.assertLess(page.index("style.css"), page.index("</head>"))
            self.assertEqual((root / "script.js").read_text(encoding="utf-8"), "// todo logic\n")
            self.assertEqual((root / "style.css").read_text(encoding="utf-8"), "/* loaded by index.html */\n")
            (root / "script.js").write_text("document.getElementById('task-input');\ndocument.getElementById('add-btn');\n", encoding="utf-8")
            defects, line = self.gate(root)
            self.assertEqual(defects, [])
            self.assertNotIn("never loaded", line)
            self.assertNotIn("CONTRACT.md says it loads", line)
            mjs = {"files": [{"path": "app/index.html", "loads": ["main.mjs"]}], "ids": [{"id": "x"}]}
            with contextlib.redirect_stdout(io.StringIO()):
                orchestrator.scaffold_contract({}, root, mjs)
            page = (root / "app" / "index.html").read_text(encoding="utf-8")
            self.assertIn('<script type="module" src="main.mjs"></script>', page)
            self.assertIn("<title>index</title>", page)
            self.assertIn('<div id="x"></div>', page)
            self.assertTrue((root / "app" / "main.mjs").is_file())

    def test_scaffold_never_touches_existing_files_and_amendments_add_only_new_pages(self):
        with tempfile.TemporaryDirectory(dir=Path(__file__).parent) as folder:
            root = self.project(folder, files={"index.html": "<p>mine</p>", "style.css": "body{}"})
            self.assertIn("[scaffold] created script.js\n", self.ensure(root))
            before = self.files(root)
            changes = {"files": [{"path": "about.html", "purpose": "About", "loads": ["style.css"]}],
                       "ids": [{"id": "about-text", "element": "p", "file": "about.html"}, {"id": "loose"}]}
            task = dict(self.TASK, contract_changes=changes)
            out = io.StringIO()
            with patch.object(orchestrator, "ask_once", return_value=json.dumps({"status": "TASK", "task": task})), contextlib.redirect_stdout(out):
                orchestrator.request_plan({}, "SYS", "USER", root, False, with_contract=True)
            self.assertIn("[scaffold] created about.html\n", out.getvalue())
            after = self.files(root)
            self.assertEqual(set(after) - set(before), {"about.html"})
            for name in before:
                if name != "CONTRACT.md":
                    self.assertEqual(after[name], before[name], name)
            about = after["about.html"].decode()
            self.assertIn('<p id="about-text"></p>', about)
            self.assertNotIn("loose", about)  # two pages now: an id without a file goes nowhere
            self.assertNotIn("task-input", about)

    def test_scaffold_false_and_contract_false_create_nothing(self):
        task = dict(self.TASK, contract_changes={"files": [{"path": "about.html", "loads": ["about.js"]}]})
        expected = {"CONTRACT.md", "index.html", "script.js", "style.css", "about.html", "about.js"}  # the default, for contrast
        for config in ({}, {"scaffold": False}, {"contract": False}):
            with tempfile.TemporaryDirectory(dir=Path(__file__).parent) as folder:
                root = self.project(folder)
                before = set(self.files(root))
                out = self.ensure(root, config)
                with patch.object(orchestrator, "ask_once", return_value=json.dumps({"status": "TASK", "task": task})), \
                        contextlib.redirect_stdout(io.StringIO()):
                    orchestrator.request_plan(config, "SYS", "USER", root, False, with_contract=True)
                created = set(self.files(root)) - before
                self.assertEqual(created, expected if not config else set() if config.get("contract") is False else {"CONTRACT.md"}, config)
                self.assertEqual("[scaffold]" in out, not config)


class RemoteRoleTests(unittest.TestCase):
    """A role on a remote OpenAI-compatible API: base_url, api_key_env, model and sampling only."""

    KEY = "sk-remote-secret-123"
    URL = "https://remote.example/api/v1"
    CONFIG = {"roles": {"planner": {"base_url": URL, "api_key_env": "REMOTE_TEST_KEY", "model": "vendor/model-x",
                                    "timeout_seconds": 30, "sampling": {"temperature": 0.2, "max_tokens": 64}}}}

    @staticmethod
    def response(payload):
        class Response:
            headers = type("H", (), {"get_content_type": staticmethod(lambda: "application/json")})()
            def __enter__(self): return self
            def __exit__(self, *args): return False
            def read(self): return json.dumps(payload).encode()
        return Response()

    def test_remote_role_completes_without_management_calls_and_sends_model(self):
        sent = []

        def fake(request, timeout=None):
            sent.append((request.full_url, json.loads(request.data) if request.data else None))
            return self.response({"choices": [{"message": {"content": "planned"}, "finish_reason": "stop"}], "usage": {}})

        for events in (False, True):
            sent.clear()
            with patch.dict(os.environ, {"REMOTE_TEST_KEY": self.KEY}), patch.object(orchestrator, "EVENTS", events), \
                    patch.object(orchestrator, "urlopen", side_effect=fake), patch.object(orchestrator, "emit") as emit, \
                    patch.object(orchestrator, "LOADED_BY_RUN", {}) as loaded, contextlib.redirect_stdout(io.StringIO()) as out:
                self.assertEqual(orchestrator.ask_once(self.CONFIG, "planner", "sys", "user", False), "planned")
                self.assertEqual(orchestrator.chat_call(self.CONFIG, "planner", "plan", "sys", [{"role": "user", "content": "hi"}]), "planned")
                orchestrator.unload_models()
            self.assertEqual([url for url, _ in sent], [f"{self.URL}/chat/completions"] * 2, events)
            self.assertTrue(all(body["model"] == "vendor/model-x" for _, body in sent))
            self.assertEqual(loaded, {})
            self.assertNotIn("loading", [c.args[0] for c in emit.call_args_list])
            self.assertNotIn(self.KEY, out.getvalue() + repr(emit.call_args_list) + repr([b for _, b in sent]))

    def test_remote_errors_name_role_and_url_never_the_key(self):
        from urllib.error import HTTPError

        for code, hint in ((401, "REMOTE_TEST_KEY"), (403, "REMOTE_TEST_KEY"), (404, "model id"), (429, "rate limited"), (503, "server error")):
            def fail(request, timeout=None):
                body = json.dumps({"error": {"message": f"Invalid key {self.KEY}"}}).encode()
                raise HTTPError(request.full_url, code, "err", {}, io.BytesIO(body))

            for events in (False, True):
                with patch.dict(os.environ, {"REMOTE_TEST_KEY": self.KEY}), patch.object(orchestrator, "EVENTS", events), \
                        patch.object(orchestrator, "urlopen", side_effect=fail), patch.object(orchestrator, "emit"), \
                        contextlib.redirect_stdout(io.StringIO()):
                    with self.assertRaises(orchestrator.WorkflowError) as ctx:
                        orchestrator.ask_once(self.CONFIG, "planner", "sys", "user", False)
                text = str(ctx.exception)
                self.assertTrue(text.startswith(f"planner: API {code} from {self.URL}/chat/completions"), text)
                self.assertIn(hint, text)
                self.assertNotIn(self.KEY, text)

    def test_custom_worker_with_only_a_model_id_is_valid(self):
        worker = {"id": "w", "title": "Remote", "kind": "reviewer", "system_prompt": "Review.", "base_url": self.URL, "model": "vendor/model-x"}
        self.assertEqual([w["id"] for w in orchestrator.custom_workers({"roles": {}, "custom_workers": [worker]})], ["w"])


if __name__ == "__main__":
    unittest.main()
