import contextlib
import io
import json
import os
import shutil
import subprocess
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch

import orchestrator

REAL_RUN_AGENT = orchestrator.run_agent
REAL_LLAMA_ALIVE = orchestrator.llama_alive


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

    def test_think_splitter_start_modes(self):
        def split(start, chunks):
            splitter = orchestrator.ThinkSplitter(start)
            pieces = [p for chunk in chunks for p in splitter.feed(chunk)] + splitter.flush()
            return {kind: "".join(t for k, t in pieces if k == kind) for kind in ("thinking", "text")}
        self.assertEqual(split(True, ["reason", "ing</th", "ink>\n\nans", "wer"]), {"thinking": "reasoning", "text": "\n\nanswer"})
        self.assertEqual(split(True, ["\n<th", "ink>r</think>a"]), {"thinking": "r", "text": "a"})  # model opened it anyway
        self.assertEqual(split(None, ["abc", "</think>ans"]), {"thinking": "abc", "text": "ans"})  # unknown template
        self.assertEqual(split(None, ["<think>x</think>y"]), {"thinking": "x", "text": "y"})
        self.assertEqual(split(None, ["hello"]), {"thinking": "", "text": "hello"})
        long = orchestrator.ThinkSplitter(None)
        self.assertEqual(long.feed("x" * 250), [("text", "x" * 250)])  # undecided only for the first AUTO_HOLD chars

    def _sse_client(self, chunks, template):
        class Response:
            headers = type("H", (), {"get_content_type": staticmethod(lambda: "text/event-stream")})()
            def __enter__(self): return self
            def __exit__(self, *args): return False
            def __iter__(self):
                for chunk in chunks:
                    yield f"data: {json.dumps({'choices': [{'delta': chunk}]})}\n".encode()
                yield b"data: [DONE]\n"

        client = orchestrator.ApiClient({"base_url": "http://127.0.0.1:1/v1", "api_key": "no-key", "auto_load": {"model_path": "q"}})
        client.role, client.llama_port = "implementer", 1
        client._request_at = lambda base, method, route, body=None: {"chat_template": template} if route == "/props" else {}
        self.enterContext(patch.dict(orchestrator.THINK_TEMPLATES, clear=True))
        return client, Response

    QWEN_TEMPLATE = "{%- if add_generation_prompt %}{{- '<|im_start|>assistant\\n<think>\\n' }}{%- endif %}"
    PLAIN_TEMPLATE = "{%- if add_generation_prompt %}{{- '<|im_start|>assistant\\n' }}{%- endif %}"

    def _stream(self, chunks, template, body=None):
        client, response = self._sse_client(chunks, template)
        with patch.object(orchestrator, "urlopen", return_value=response()), patch.object(orchestrator, "emit") as emit:
            message = client._stream_chat(body or {"model": "m", "messages": []})["choices"][0]["message"]
        return message, [(c.kwargs["kind"], c.kwargs["text"]) for c in emit.call_args_list]

    def test_stream_starts_in_thinking_when_the_template_opens_think(self):
        chunks = [{"content": "reason"}, {"content": "ing…</think>"}, {"content": "\n\nanswer"}]
        message, deltas = self._stream(chunks, self.QWEN_TEMPLATE)
        self.assertEqual("".join(t for k, t in deltas if k == "thinking"), "reasoning…")
        self.assertEqual([k for k, _ in deltas], ["thinking", "thinking", "text"])
        self.assertEqual(orchestrator.answer_text(message), "answer")
        # thinking disabled for this call: not started in thinking, all text; split_reasoning still splits a bare </think>
        off = {"model": "m", "messages": [], "chat_template_kwargs": {"enable_thinking": False}}
        message, deltas = self._stream(chunks, self.QWEN_TEMPLATE, off)
        self.assertEqual({k for k, _ in deltas}, {"text"})
        self.assertEqual(orchestrator.split_reasoning(message), ("reasoning…", "answer"))

    def test_stream_stays_text_for_a_non_thinking_template_and_keeps_reasoning_content(self):
        message, deltas = self._stream([{"content": "plain "}, {"content": "answer"}], self.PLAIN_TEMPLATE)
        self.assertEqual((deltas, message["content"]), ([("text", "plain "), ("text", "answer")], "plain answer"))
        # llama-server's own reasoning parser: reasoning_content first, then the answer as content
        message, deltas = self._stream([{"reasoning_content": "hm"}, {"content": "hi"}], self.QWEN_TEMPLATE)
        self.assertEqual((deltas, message["content"], message["reasoning_content"]), ([("thinking", "hm"), ("text", "hi")], "hi", "hm"))

    def test_split_reasoning_cases(self):
        split = orchestrator.split_reasoning
        self.assertEqual(split({"content": "plan</think>\n\nanswer"}), ("plan", "answer"))
        self.assertEqual(split({"content": "<think>plan</think>answer"}), ("plan", "answer"))
        self.assertEqual(split({"content": "answer", "reasoning_content": "r"}), ("r", "answer"))
        self.assertEqual(split({"content": "just text"}), ("", "just text"))
        self.assertTrue(orchestrator.opens_think(self.QWEN_TEMPLATE))
        self.assertFalse(orchestrator.opens_think(self.PLAIN_TEMPLATE) or orchestrator.opens_think("<think> no prompt"))

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

    OK_REPLY = {"choices": [{"message": {"content": "ok"}, "finish_reason": "stop"}]}
    OVERFLOW = "implementer: API 400 from http://x/v1/chat/completions: context length exceeded"

    def _long_history(self):
        """system, task, five read_file steps with big results, then one step with two calls and their two results."""
        messages = [{"role": "system", "content": "sys"}, {"role": "user", "content": "the task"}]
        for i in range(5):
            call = {"id": f"c{i}", "type": "function", "function": {"name": "read_file", "arguments": json.dumps({"path": f"f{i}.py"})}}
            messages += [{"role": "assistant", "content": "", "tool_calls": [call]}, {"role": "tool", "tool_call_id": f"c{i}", "content": "x" * 700}]
        calls = [{"id": f"d{i}", "type": "function", "function": {"name": "search_files", "arguments": json.dumps({"pattern": "def"})}} for i in range(2)]
        messages.append({"role": "assistant", "content": "", "tool_calls": calls})
        messages += [{"role": "tool", "tool_call_id": f"d{i}", "content": "y" * 100} for i in range(2)]
        return messages

    def _compacting_client(self, n_ctx, summary, main):
        """A client with context size n_ctx; summary calls get `summary` (raised if an exception), others call main(n)."""
        client = orchestrator.ApiClient({"base_url": "http://x/v1", "api_key": "k"})
        client.role, client.context_size = "implementer", lambda: n_ctx
        sent = []

        def answer(via, body):
            body = json.loads(json.dumps(body))  # the messages list is mutated in place later
            sent.append((via, body))
            if body["messages"][0]["content"] == orchestrator.COMPACT_PROMPT:
                if isinstance(summary, Exception):
                    raise summary
                return {"choices": [{"message": {"content": summary}, "finish_reason": "stop"}]}
            return main(sum(1 for _, b in sent if b["messages"][0]["content"] != orchestrator.COMPACT_PROMPT))

        client._request = lambda method, route, body=None: answer("request", body)
        client._stream_chat = lambda body: answer("stream", body)
        return client, sent

    def test_complete_compacts_a_history_over_the_threshold(self):
        messages = self._long_history()
        tail = messages[-5:]  # the f4 step and the last one: what half the room leaves after system, task and summary
        client, sent = self._compacting_client(1000, "SUMMARY", lambda n: self.OK_REPLY)
        events, out = [], io.StringIO()
        with patch.object(orchestrator, "EVENTS", True), patch.object(orchestrator, "emit", lambda e, **d: events.append((e, d))), \
                patch.object(orchestrator.ApiClient, "_monitor_row", lambda client, started: {}), contextlib.redirect_stdout(out):
            self.assertEqual(client.complete("m", messages, {"max_tokens": 100})["content"], "ok")
        # one quiet summary call (no streaming, thinking off, its own max_tokens), then the compacted request
        self.assertEqual([via for via, _ in sent], ["request", "stream"])
        self.assertEqual((sent[0][1]["max_tokens"], sent[0][1]["enable_thinking"]), (200, False))  # scaled: room 900 → the 200 floor
        self.assertIn("f0.py", sent[0][1]["messages"][1]["content"])
        self.assertEqual(messages[0], {"role": "system", "content": "sys"})
        self.assertEqual(messages[1], {"role": "user", "content": "the task" + orchestrator.COMPACT_MARK + "SUMMARY"})
        self.assertEqual(messages[2:], tail)  # starts at an assistant message, its two tool results kept with it
        self.assertEqual(sent[1][1]["messages"], messages)
        compacted = [d for e, d in events if e == "compacted"]
        self.assertEqual(len(compacted), 1)
        self.assertEqual(compacted[0]["role"], "implementer")
        self.assertGreater(compacted[0]["before"], 720)
        self.assertLess(compacted[0]["after"], compacted[0]["before"])
        self.assertIn(f"[implementer] context compacted: {compacted[0]['before']} → {compacted[0]['after']} tokens", out.getvalue())
        self.assertEqual([d.get("text") for e, d in events if e == "message"], ["ok"])  # nothing of the summary streamed

    def test_context_error_compacts_once_and_retries(self):
        def overflow_first(n):
            if n == 1:
                raise orchestrator.WorkflowError(self.OVERFLOW)
            return self.OK_REPLY

        messages = self._long_history()
        client, sent = self._compacting_client(None, "SUMMARY", overflow_first)  # unknown n_ctx: only the error triggers it
        with patch.object(orchestrator, "EVENTS", False), contextlib.redirect_stdout(io.StringIO()):
            self.assertEqual(client.complete("m", messages, {})["content"], "ok")
        self.assertEqual(len(sent), 3)  # failed request, summary, retry
        self.assertIn(orchestrator.COMPACT_MARK + "SUMMARY", messages[1]["content"])
        self.assertEqual(messages[2]["role"], "assistant")
        self.assertEqual(sent[2][1]["messages"], messages)
        # still too long after compacting: the existing error, after one compaction only (the kept tail holds several
        # small steps, so a second compaction would have a middle to drop)
        messages = self._long_history()
        for i in range(4):
            call = {"id": f"e{i}", "type": "function", "function": {"name": "read_file", "arguments": json.dumps({"path": f"g{i}.py"})}}
            messages += [{"role": "assistant", "content": "", "tool_calls": [call]}, {"role": "tool", "tool_call_id": f"e{i}", "content": "w" * 20}]
        client, sent = self._compacting_client(None, "SUMMARY", lambda n: (_ for _ in ()).throw(orchestrator.WorkflowError(self.OVERFLOW)))
        with patch.object(orchestrator, "EVENTS", False), contextlib.redirect_stdout(io.StringIO()):
            with self.assertRaises(orchestrator.WorkflowError) as ctx:
                client.complete("m", messages, {})
        self.assertIn("exceeded the model's context length", str(ctx.exception))
        self.assertGreaterEqual(sum(1 for m in messages if m["role"] == "assistant"), 2)
        self.assertEqual(sum(1 for _, b in sent if b["messages"][0]["content"] == orchestrator.COMPACT_PROMPT), 1)

    def test_compaction_falls_back_to_a_tool_call_note_when_the_summary_fails(self):
        messages = self._long_history()
        client, _ = self._compacting_client(1000, orchestrator.WorkflowError("API 500"), lambda n: self.OK_REPLY)
        with patch.object(orchestrator, "EVENTS", False), contextlib.redirect_stdout(io.StringIO()):
            client.complete("m", messages, {"max_tokens": 100})
        summary = messages[1]["content"].partition(orchestrator.COMPACT_MARK)[2]
        self.assertIn("Tool calls made", summary)
        self.assertIn("- read_file f0.py", summary)
        self.assertIn("- read_file f3.py", summary)
        self.assertEqual(messages[2]["role"], "assistant")

    def test_compaction_fallback_lists_text_mode_tool_calls(self):
        messages = self._long_history()
        for m in messages:  # a template without tool calling: the calls are printed as JSON text
            if m["role"] == "assistant":
                m["content"] = "\n".join(json.dumps({"name": c["function"]["name"], "arguments": json.loads(c["function"]["arguments"])})
                                         for c in m.pop("tool_calls"))
        client, _ = self._compacting_client(1000, orchestrator.WorkflowError("API 500"), lambda n: self.OK_REPLY)
        with patch.object(orchestrator, "EVENTS", False), contextlib.redirect_stdout(io.StringIO()):
            client.complete("m", messages, {"max_tokens": 100})
        summary = messages[1]["content"].partition(orchestrator.COMPACT_MARK)[2]
        self.assertIn("- read_file f0.py", summary)
        self.assertNotIn("- none", summary)

    def test_summary_call_drops_reasoning_effort(self):
        client, sent = self._compacting_client(1000, "SUMMARY", lambda n: self.OK_REPLY)
        with patch.object(orchestrator, "EVENTS", False), contextlib.redirect_stdout(io.StringIO()):
            client.complete("m", self._long_history(), {"max_tokens": 100, "enable_thinking": True, "reasoning_effort": "high"})
        self.assertEqual(sent[0][1]["messages"][0]["content"], orchestrator.COMPACT_PROMPT)
        self.assertNotIn("reasoning_effort", sent[0][1])
        self.assertEqual(sent[1][1]["reasoning_effort"], "high")  # the step itself keeps it

    def test_second_compaction_passes_the_earlier_summary_whole(self):
        messages = self._long_history()
        earlier = "p" * 5000 + "\nWhat remains: END-OF-SUMMARY"
        messages[1]["content"] += orchestrator.COMPACT_MARK + earlier
        client, sent = self._compacting_client(1000, "SUMMARY", lambda n: self.OK_REPLY)
        with patch.object(orchestrator, "EVENTS", False), contextlib.redirect_stdout(io.StringIO()):
            client.complete("m", messages, {"max_tokens": 100})
        self.assertIn(earlier, sent[0][1]["messages"][1]["content"])

    def test_summary_prompt_fits_half_the_room_with_task_and_system(self):
        messages = self._long_history()
        messages[1]["content"] = "z" * 5000
        messages[2:2] = json.loads(json.dumps(messages[2:12]))  # ten big steps
        client, sent = self._compacting_client(4100, "SUMMARY", lambda n: self.OK_REPLY)  # room 4000
        with patch.object(orchestrator, "EVENTS", False), contextlib.redirect_stdout(io.StringIO()):
            client.complete("m", messages, {"max_tokens": 100})
        prompt = sent[0][1]["messages"]
        self.assertEqual(prompt[0]["content"], orchestrator.COMPACT_PROMPT)
        self.assertIn("...[trimmed]...", prompt[1]["content"])
        self.assertLessEqual(sum(len(m["content"]) for m in prompt), 0.5 * 4000 * 3.5 + 50)  # 50: the trim marker

    def test_large_task_does_not_compact_on_every_step(self):
        """A 40k-char task (the implementer embeds the allowed files), n_ctx 32768, max_tokens 8192, 30 tool steps."""
        tools = [{"type": "function", "function": {"name": "read_file", "parameters": {"type": "object"}}}]
        messages = [{"role": "system", "content": "s" * 2000}, {"role": "user", "content": "t" * 40_000}]
        client, sent = self._compacting_client(32768, "S" * 2000, lambda n: self.OK_REPLY)
        summaries = lambda: sum(1 for _, b in sent if b["messages"][0]["content"] == orchestrator.COMPACT_PROMPT)
        compacted_at = []
        with patch.object(orchestrator, "EVENTS", False), contextlib.redirect_stdout(io.StringIO()):
            for step in range(30):
                done = summaries()
                client.complete("m", messages, {"max_tokens": 8192}, tools)
                if summaries() > done:
                    compacted_at.append(step)
                call = {"id": f"c{step}", "type": "function", "function": {"name": "read_file", "arguments": json.dumps({"path": f"f{step}.py"})}}
                messages += [{"role": "assistant", "content": "", "tool_calls": [call]},
                             {"role": "tool", "tool_call_id": f"c{step}", "content": "r" * 6000}]
        self.assertTrue(compacted_at)
        self.assertFalse([s for s in compacted_at if s - 1 in compacted_at], compacted_at)  # never on consecutive steps
        self.assertLessEqual(len(compacted_at), 8, compacted_at)

    def test_too_short_history_is_not_compacted_and_the_error_stands(self):
        call = {"id": "c", "type": "function", "function": {"name": "read_file", "arguments": "{}"}}
        messages = [{"role": "system", "content": "sys"}, {"role": "user", "content": "z" * 5000},
                    {"role": "assistant", "content": "", "tool_calls": [call]}, {"role": "tool", "tool_call_id": "c", "content": "r"}]
        original = json.loads(json.dumps(messages))
        client, sent = self._compacting_client(1000, "SUMMARY", lambda n: (_ for _ in ()).throw(orchestrator.WorkflowError(self.OVERFLOW)))
        with patch.object(orchestrator, "EVENTS", False), contextlib.redirect_stdout(io.StringIO()):
            with self.assertRaises(orchestrator.WorkflowError) as ctx:
                client.complete("m", messages, {"max_tokens": 100})
        self.assertIn("exceeded the model's context length", str(ctx.exception))
        self.assertEqual(messages, original)
        self.assertEqual(len(sent), 1)  # no summary call

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
        argv = patch.object(orchestrator.sys, "argv", ["o", "--project", str(root), *(["--once"] if once else []), "--non-interactive", "--pipeline"])
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

        argv = ["o", "--project", str(root), "--non-interactive", "--pipeline", *(["--once"] if once else [])]
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

    def chat(self, request, replies=None, workers=None, role_prompts=None):
        replies = {k: list(v) for k, v in (replies or {}).items()}
        roles = {r: {"base_url": "http://x", "expected_model_contains": f"{r}-model", "sampling": {"temperature": i}}
                 for i, r in enumerate(("planner", "implementer", "reviewer"))}
        for r, text in (role_prompts or {}).items():
            roles[r]["chat_system_prompt"] = text
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
                reply = replies[self.role].pop(0) if replies.get(self.role) else f"{self.role} text"
                return reply if isinstance(reply, dict) else {"role": "assistant", "content": reply}

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
        self.assertEqual((log[0]["model"], log[0]["sampling"]), ("implementer-model", {"temperature": 1}))
        self.assertEqual([t["function"]["name"] for t in log[0]["tools"]], ["delegate"])  # always offered
        self.assertIn((Path(orchestrator.ROOT) / "prompts" / "chat.txt").read_text(encoding="utf-8").strip(), log[0]["system"])
        self.assertIn("Implementer worker", log[0]["system"])
        self.assertIn("explain X", log[0]["input"])
        self.assertEqual([e for e in events if e[0].startswith("chat_")],
                         [("chat_stage", {"role": "implementer", "stage": "answer", "title": "implementer"}),
                          ("chat_reply", {"role": "implementer", "text": "the answer"})])
        # G3: no workflow files, models stay loaded
        self.assertEqual(files, ["config.json", "request.json"])
        self.assertEqual(unloaded, [])

    def test_builtin_chat_system_prompt_is_optional_and_appended(self):
        chat_txt = (Path(orchestrator.ROOT) / "prompts" / "chat.txt").read_text(encoding="utf-8").strip()
        _, log, *_ = self.chat({"mode": "single", "worker": "implementer", "messages": self.MSGS},
                               role_prompts={"implementer": "  Answer in French.  "})
        self.assertTrue(log[0]["system"].startswith(chat_txt))
        self.assertIn("=== YOUR ROLE: Implementer ===\nAnswer in French.\n", log[0]["system"])

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

    def test_effort_sets_thinking_on_every_call_of_the_turn_but_not_the_title(self):
        high = {"temperature": 1, "enable_thinking": True, "reasoning_effort": "high"}
        _, log, *_ = self.chat({"mode": "single", "worker": "implementer", "messages": self.MSGS, "effort": "high", "title": True},
                               {"implementer": ["the answer", "A Title"]})
        self.assertEqual(log[0]["sampling"], high)
        self.assertEqual((log[1]["sampling"]["enable_thinking"], "reasoning_effort" in log[1]["sampling"]), (False, False))
        _, log, *_ = self.chat({"mode": "single", "worker": "implementer", "messages": self.MSGS, "effort": "off"})
        self.assertEqual(log[0]["sampling"], {"temperature": 1, "enable_thinking": False})
        for effort in ("", "bogus", None):
            _, log, *_ = self.chat({"mode": "single", "worker": "implementer", "messages": self.MSGS, "effort": effort})
            self.assertEqual(log[0]["sampling"], {"temperature": 1}, effort)

    def test_legacy_mode_is_ignored_and_worker_defaults_to_planner(self):
        for request in ({"mode": "multi", "messages": self.MSGS}, {"mode": "single", "messages": self.MSGS}, {"messages": self.MSGS}):
            code, log, events, *_ = self.chat(request, {"planner": ["ANSWER"]})
            self.assertEqual((code, [l["model"] for l in log]), (0, ["planner-model"]), request)
            self.assertEqual([d for e, d in events if e == "chat_reply"], [{"role": "planner", "text": "ANSWER"}])

    def test_delegate_runs_a_fresh_sub_agent_with_only_the_extras_and_is_capped(self):
        brief = lambda n: tool_call("delegate", worker="reviewer" if n == 2 else "implementer", brief=f"BRIEF{n}")
        replies = {"planner": [brief(i) for i in range(4)] + ["FINAL"],
                   "implementer": [tool_call("delegate", worker="reviewer", brief="nested"), "REPORT0", "REPORT1"],
                   "reviewer": ["REVIEW2"]}
        code, log, events, *_ = self.chat({"messages": self.MSGS, "tools": True}, replies)
        self.assertEqual(code, 0)
        self.assertEqual([d for e, d in events if e == "chat_reply"], [{"role": "planner", "text": "FINAL"}])
        sub = [l for l in log if "BRIEF" in l["input"]]
        self.assertTrue(sub and all("explain X" not in l["input"] for l in sub))  # fresh context: the brief only
        self.assertIn("implementer working for a lead agent", sub[0]["system"])
        self.assertIn("UTILITIES", sub[0]["system"])
        self.assertEqual([t["function"]["name"] for t in sub[0]["tools"]], ["calculator", "current_time", "fetch_page"])  # no delegate
        results = [d["result"] for e, d in events if e == "tool" and d["name"] == "delegate"]
        self.assertTrue(results[0].startswith("ERROR: tool 'delegate' is unavailable"))  # the sub-agent cannot delegate
        self.assertEqual(results[1:], ["REPORT0", "REPORT1", "REVIEW2", "ERROR: delegation limit reached (3 per turn); finish the work yourself"])
        stages = [(d["role"], d["stage"]) for e, d in events if e == "chat_stage"]
        self.assertEqual(stages, [("planner", "answer")] + [("implementer", "delegate"), ("planner", "answer")] * 2
                         + [("reviewer", "delegate"), ("planner", "answer")])

    def test_missing_or_same_as_builtin_falls_back_to_the_planner(self):
        config = {"roles": {"planner": {"base_url": "http://x", "api_key": "k", "expected_model_contains": "planner-model",
                                        "sampling": {"temperature": 0.5}, "auto_load": {"model_path": "p"}},
                            "reviewer": {"same_as": "planner", "chat_system_prompt": "Be strict."}}}
        for role in ("implementer", "reviewer"):
            client, sampling = orchestrator.role_settings(config, role)
            self.assertEqual((client.expected, sampling, client.auto_load, client.role), ("planner-model", {"temperature": 0.5}, {"model_path": "p"}, role))
            self.assertIsNone(orchestrator.chat_worker(config, role))
        self.assertIn("Reviewer: p\n", orchestrator.team_context(config))  # the planner's auto_load model
        self.assertIn("Be strict.", orchestrator.role_block(config, "reviewer"))
        own = {"roles": {**config["roles"], "implementer": {"base_url": "http://x", "api_key": "k", "expected_model_contains": "impl-model"}}}
        self.assertEqual(orchestrator.role_settings(own, "implementer")[0].expected, "impl-model")
        for bad in ({"roles": {}}, {"roles": {"implementer": {"base_url": "http://x", "api_key": "k"}}}):
            with self.assertRaisesRegex(orchestrator.WorkflowError, "planner"):
                orchestrator.role_settings(bad, "reviewer")
            with self.assertRaisesRegex(orchestrator.WorkflowError, "no planner to fall back to"):
                orchestrator.chat_worker(bad, "reviewer")


class ConversationCompactionTests(unittest.TestCase):
    """Chat/Code turns summarize a long history before the turn (compact_conversation) and apply a stored summary."""

    ESSAY = "word " * 2000  # a 2000-word reply: ~2.9k estimated tokens

    def turn(self, request, n_ctx=8192, summary="SUMMARY TEXT", max_tokens=4096, base=object, settings=None):
        """One --chat turn against fakes with context window n_ctx; returns (calls, events). `base` (with `settings` merged
        into every role) swaps in a real client class whose own window and loading are under test."""
        calls, events = [], []

        class Fake(base):
            if base is object:
                def __init__(self, settings):
                    self.settings, self.role, self.title = settings, "", ""

                def window_before_load(self):
                    return n_ctx

                def ensure_model(self, role, interactive):
                    return "m"

            def complete(self, model, messages, sampling, tools=None):
                calls.append({"messages": list(messages), "sampling": sampling})  # as sent: the tool loop appends the reply after
                if messages[0]["content"] == orchestrator.CONVERSATION_PROMPT:
                    if isinstance(summary, Exception):
                        raise summary
                    return {"role": "assistant", "content": summary}
                return {"role": "assistant", "content": "the answer"}

        roles = {r: {"base_url": "http://x", "sampling": {"max_tokens": max_tokens}, **(settings or {})} for r in orchestrator.BUILTIN_ROLES}
        out = io.StringIO()
        out.reconfigure = lambda **kw: None
        with tempfile.TemporaryDirectory() as folder:
            (Path(folder) / "config.json").write_text(json.dumps({"roles": roles}), encoding="utf-8")
            req = Path(folder) / "request.json"
            req.write_text(json.dumps(request), encoding="utf-8")
            argv = ["o", "--project", folder, "--chat", str(req), "--events", "--non-interactive"]
            record = lambda e, **d: events.append((e, d)) if orchestrator.EVENTS else None  # as emit: nothing while silenced
            with patch.object(orchestrator, "ApiClient", Fake), patch.object(orchestrator, "emit", record), \
                    patch.object(orchestrator, "unload_models", lambda: None), patch.object(orchestrator.sys, "argv", argv), \
                    patch.object(orchestrator.sys, "stdout", out), patch.dict(orchestrator.LOADED_BY_RUN, clear=True), \
                    patch.dict(orchestrator.LOADED_NOW, clear=True):
                self.assertEqual(orchestrator.main(), 0)
        return calls, events

    def unsloth_turn(self):
        """A --chat turn on a real client against a fake Unsloth that holds another model with a 32k window; the role
        loads its own at 8k. Returns (calls, events, CONTEXT_SIZES after the turn)."""
        server = {"model": "other-model", "ctx": 32768}

        def request_at(client, base, method, route, body=None):
            if route == "/models":
                return {"data": [{"id": server["model"]}]}
            if route == "/api/inference/status":
                return {"context_length": server["ctx"], "requested_context_length": server["ctx"]}
            if route == "/api/inference/load":
                server.update(model=body["model_path"], ctx=body["max_seq_length"])
                return {}
            return {"entries": []}

        settings = {"base_url": "http://u/v1", "management_url": "http://u", "api_key": "k", "expected_model_contains": "mine",
                    "auto_load": {"model_path": "org/mine", "max_seq_length": 8192}}
        with patch.object(orchestrator.ApiClient, "_request_at", request_at), patch.dict(orchestrator.CONTEXT_SIZES, clear=True):
            calls, events = self.turn({"mode": "single", "worker": "implementer", "messages": self.history()},
                                      base=orchestrator.ApiClient, settings=settings)
            return calls, events, dict(orchestrator.CONTEXT_SIZES)

    def history(self, last_reply=None):
        messages = []
        for i in range(4):
            messages += [{"role": "user", "content": f"Write essay {i} please"}, {"role": "assistant", "content": f"ESSAY{i} " + self.ESSAY}]
        if last_reply is not None:
            messages[-1]["content"] = last_reply
        return messages + [{"role": "user", "content": "What is 2 + 2?"}]

    def test_long_chat_is_summarized_before_the_turn(self):
        calls, events = self.turn({"mode": "single", "worker": "implementer", "messages": self.history()})
        summary_call, answer = calls
        self.assertEqual(summary_call["messages"][0]["content"], orchestrator.CONVERSATION_PROMPT)
        self.assertEqual(summary_call["sampling"]["enable_thinking"], False)
        self.assertLessEqual(summary_call["sampling"]["max_tokens"], 512)  # 8192 window, 4096 reply: room 4096 → 512
        self.assertIn("Write essay 0", summary_call["messages"][1]["content"])
        # the answer call: system + the latest user message only (the long last reply does not fit 20% of the room)
        sent = answer["messages"][1:]
        self.assertEqual(len(sent), 1)
        self.assertEqual(sent[0]["content"], "Earlier in this conversation (summary):\nSUMMARY TEXT\n\nCurrent message:\nWhat is 2 + 2?")
        self.assertNotIn("compact", sent[0]["content"].lower())
        compacted = [d for e, d in events if e == "history_compacted"]
        self.assertEqual(len(compacted), 1)
        self.assertEqual((compacted[0]["summary"], compacted[0]["covers"]), ("SUMMARY TEXT", 8))
        self.assertLess(compacted[0]["after"], 0.2 * compacted[0]["before"])

    def test_a_short_last_reply_is_kept_verbatim(self):
        calls, events = self.turn({"mode": "single", "worker": "implementer", "messages": self.history("Short reply.")})
        sent = calls[1]["messages"][1:]
        self.assertEqual([m["role"] for m in sent], ["user", "assistant", "user"])  # roles stay user-first
        self.assertEqual(sent[0]["content"], "Earlier in this conversation (summary):\nSUMMARY TEXT")
        self.assertEqual(sent[1:], self.history("Short reply.")[-2:])
        self.assertEqual([d["covers"] for e, d in events if e == "history_compacted"], [7])

    def test_stored_summary_and_trimmed_messages_are_applied(self):
        msgs = [{"role": "user", "content": "Next question"}]
        calls, events = self.turn({"mode": "single", "worker": "implementer", "messages": msgs, "summary": "OLD", "summary_covers": 8})
        self.assertEqual(len(calls), 1)  # small: no summary call
        self.assertEqual(calls[0]["messages"][1:], [{"role": "user", "content": "Earlier in this conversation (summary):\nOLD\n\nCurrent message:\nNext question"}])
        self.assertFalse([e for e, _ in events if e == "history_compacted"])
        # a later compaction folds the stored summary in; covers counts from the full history
        calls, events = self.turn({"mode": "single", "worker": "implementer", "messages": self.history(), "summary": "OLD", "summary_covers": 3})
        self.assertIn("EARLIER SUMMARY\nOLD", calls[0]["messages"][1]["content"])
        self.assertEqual([d["covers"] for e, d in events if e == "history_compacted"], [11])

    def test_small_or_unknown_window_is_not_compacted(self):
        for n_ctx in (131072, None):
            calls, events = self.turn({"mode": "single", "worker": "implementer", "messages": self.history()}, n_ctx=n_ctx)
            self.assertEqual(len(calls), 1)
            self.assertEqual(calls[0]["messages"][1:], self.history())
            self.assertFalse([e for e, _ in events if e == "history_compacted"])

    def test_failed_summary_sends_the_full_history(self):
        calls, events = self.turn({"mode": "single", "worker": "implementer", "messages": self.history()},
                                  summary=orchestrator.WorkflowError("API 500"))
        self.assertEqual(calls[1]["messages"][1:], self.history())
        self.assertFalse([e for e, _ in events if e == "history_compacted"])

    def test_window_before_the_load_is_the_roles_not_the_loaded_models(self):
        calls, events, sizes = self.unsloth_turn()
        self.assertEqual(calls[0]["messages"][0]["content"], orchestrator.CONVERSATION_PROMPT)  # 8k, not the other model's 32k
        self.assertEqual([d["covers"] for e, d in events if e == "history_compacted"], [8])
        self.assertNotIn(32768, sizes.values())  # no pre-load guess cached for the turn

    def test_summary_model_load_is_reported_to_the_launcher(self):
        calls, events, _ = self.unsloth_turn()
        names = [e for e, _ in events]
        self.assertIn(("loading", {"role": "implementer", "path": "org/mine", "url": "http://u"}), events)
        self.assertLess(names.index("loading"), names.index("history_compacted"))  # the summary call's load, not the answer's
        self.assertIn("model", names[:names.index("history_compacted")])

    def test_stored_summary_before_an_earlier_exchange_is_not_the_current_message(self):
        msgs = [{"role": "user", "content": "First"}, {"role": "assistant", "content": "Reply"}, {"role": "user", "content": "Next"}]
        calls, _ = self.turn({"mode": "single", "worker": "implementer", "messages": msgs, "summary": "OLD", "summary_covers": 4})
        self.assertEqual(calls[0]["messages"][1:], [{"role": "user", "content": "Earlier in this conversation (summary):\nOLD\n\n---\n\nFirst"},
                                                     *msgs[1:]])


def tool_call(name, **arguments):
    return {"role": "assistant", "content": "", "tool_calls": [{"id": "c1", "function": {"name": name, "arguments": json.dumps(arguments)}}]}


class CodeTests(unittest.TestCase):
    """--code: one worker with file tools confined to the request's folders (folders[0] = project root)."""

    def setUp(self):
        tmp = tempfile.TemporaryDirectory()
        self.addCleanup(tmp.cleanup)
        base = Path(tmp.name).resolve()
        self.root, self.extra, self.shared, self.cfg = (base / n for n in ("app", "out", "lib", "cfg"))
        for folder in (self.root, self.extra, self.shared, self.cfg):
            folder.mkdir()
        self.folders = [{"path": str(self.root), "access": "write"}, {"path": str(self.extra), "access": "write"},
                        {"path": str(self.shared), "access": "read"}]
        self.config = {"roles": {r: {"base_url": "http://x", "expected_model_contains": f"{r}-model"} for r in orchestrator.BUILTIN_ROLES}}

    def code(self, request, replies, n_ctx=None):
        request = {"folders": self.folders, **request}
        replies = {k: list(v) for k, v in replies.items()}
        log, events, unloaded = [], [], []

        class Fake:
            def __init__(self, settings):
                self.settings, self.role, self.title = settings, "", ""

            def window_before_load(self):
                return n_ctx

            def ensure_model(self, role, interactive):
                return "m"

            def complete(self, model, messages, sampling, tools=None):
                log.append({"role": self.role, "system": messages[0]["content"], "messages": list(messages[1:]), "tools": tools, "sampling": sampling})
                reply = replies[self.role].pop(0)
                return {"role": "assistant", "content": reply} if isinstance(reply, str) else reply

        (self.cfg / "config.json").write_text(json.dumps(self.config), encoding="utf-8")
        req = self.cfg / "request.json"
        req.write_text(json.dumps(request), encoding="utf-8")
        argv = ["o", "--project", str(self.cfg), "--code", str(req), "--events"]
        with patch.object(orchestrator, "ApiClient", Fake), patch.object(orchestrator, "emit", lambda e, **d: events.append((e, d))), \
                patch.object(orchestrator, "unload_models", lambda: unloaded.append(1)), patch.object(orchestrator.sys, "argv", argv):
            code = orchestrator.main()
        return code, log, events, unloaded

    MSGS = [{"role": "user", "content": "add a readme"}]

    def test_builtin_roles_keep_the_fixed_lead_prompt(self):
        # chat_system_prompt is for the Chat view: a Code turn's lead keeps prompts/lead.txt.
        self.config["roles"]["planner"]["chat_system_prompt"] = "Chat only."
        code, log, *_ = self.code({"action": "chat", "messages": self.MSGS}, {"planner": ["done"]})
        self.assertEqual(code, 0)
        self.assertTrue(log[0]["system"].startswith((Path(orchestrator.ROOT) / "prompts" / "lead.txt").read_text(encoding="utf-8").strip()))
        self.assertNotIn("Chat only.", log[0]["system"])

    def test_long_code_chat_is_summarized_before_the_turn(self):
        history = []
        for i in range(4):
            history += [{"role": "user", "content": f"Change {i}"}, {"role": "assistant", "content": f"DIFF{i} " + "line " * 2000}]
        code, log, events, _ = self.code({"action": "chat", "messages": history + self.MSGS},
                                         {"planner": ["SUMMARY TEXT", "done"]}, n_ctx=8192)
        self.assertEqual(code, 0)
        self.assertEqual(log[0]["system"], orchestrator.CONVERSATION_PROMPT)
        self.assertEqual(log[1]["messages"], [{"role": "user", "content": "Earlier in this conversation (summary):\nSUMMARY TEXT\n\nCurrent message:\nadd a readme"}])
        self.assertEqual([(d["summary"], d["covers"]) for e, d in events if e == "history_compacted"], [("SUMMARY TEXT", 8)])

    def test_effort_applies_to_the_code_turn_but_not_the_title(self):
        code, log, *_ = self.code({"action": "chat", "messages": self.MSGS, "effort": "max", "title": True},
                                  {"planner": ["done", "Readme"]})
        self.assertEqual(code, 0)
        self.assertEqual(log[0]["sampling"], {"enable_thinking": True, "reasoning_effort": "max"})
        self.assertEqual(log[1]["sampling"]["enable_thinking"], False)
        self.assertNotIn("reasoning_effort", log[1]["sampling"])

    def test_request_validation(self):
        good = {"action": "chat", "worker": "implementer", "messages": self.MSGS, "folders": self.folders}
        cases = [
            ([1], "must be a JSON object"),
            ({**good, "action": "run"}, "Unknown code action"),
            ({**good, "messages": [{"role": "assistant", "content": "x"}]}, "ending with a user message"),
            ({**good, "action": "write_project", "worker": "nobody"}, "Unknown chat worker"),  # chat ignores worker
            ({**good, "folders": []}, "at least one folder"),
            ({**good, "folders": [{"path": "relative/dir", "access": "write"}]}, "Folder 1 is not an existing absolute"),
            ({**good, "folders": [{"path": str(self.root / "missing"), "access": "write"}]}, "Folder 1 is not an existing absolute"),
            ({**good, "folders": [self.folders[0], {"path": str(self.shared), "access": "all"}]}, "Folder 2 access"),
            ({**good, "folders": [{"path": str(orchestrator.ROOT), "access": "write"}]}, "Folder 1 overlaps the Agent Orchestrator"),
            ({**good, "folders": [self.folders[0], {"path": str(orchestrator.ROOT / "prompts"), "access": "write"}]}, "Folder 2 overlaps"),
            ({**good, "folders": [self.folders[0], {"path": str(orchestrator.ROOT.parent), "access": "write"}]}, "Folder 2 overlaps"),
        ]
        for request, message in cases:
            req = self.cfg / "r.json"
            req.write_text(json.dumps(request), encoding="utf-8")
            with self.subTest(message), self.assertRaisesRegex(orchestrator.WorkflowError, message):
                orchestrator.run_code(self.config, req)

    def test_code_path_confinement(self):
        path = lambda raw, write=False: orchestrator.code_path(raw, self.folders, write)
        self.assertEqual(path("src/a.py", True), self.root / "src" / "a.py")
        self.assertEqual(path(str(self.shared / "x.py")), self.shared / "x.py")
        self.assertEqual(path(str(self.extra / "y.txt"), True), self.extra / "y.txt")
        self.assertEqual(path("../lib/x.py"), self.shared / "x.py")  # `..` into another granted folder is fine
        refused = [("../cfg/config.json", False, "outside"), ("src/../../cfg/config.json", False, "outside"), (str(self.cfg / "a"), False, "outside"),
                   (str(self.shared / "x.py"), True, "read-only"), ("api_key.txt", False, "protected"),
                   (str(self.shared / "API_KEY.txt"), False, "protected"), (".git/config", False, "protected"), ("sub/.git/HEAD", True, "protected")]
        for raw, write, message in refused:
            with self.subTest(raw), self.assertRaisesRegex(orchestrator.WorkflowError, message):
                path(raw, write)
        nested = self.folders + [{"path": str(self.root / "vendor"), "access": "read"}]  # the innermost folder decides
        with self.assertRaisesRegex(orchestrator.WorkflowError, "read-only"):
            orchestrator.code_path("vendor/v.js", nested, True)

    def test_list_search_and_read_caps(self):
        for name in ("a.txt", "b.txt", "c.txt", "node_modules/x.txt", ".git/config"):
            (self.root / name).parent.mkdir(parents=True, exist_ok=True)
            (self.root / name).write_text("needle here\n", encoding="utf-8")
        (self.root / "api_key.txt").write_text("needle SECRET\n", encoding="utf-8")
        tools = orchestrator.code_handlers(self.folders)
        listing = tools["list_files"]()
        self.assertIn("a.txt", listing)
        self.assertNotIn("node_modules", listing)
        self.assertNotIn(".git", listing)
        hits = tools["search_files"]("needle")
        self.assertEqual(hits.splitlines(), ["a.txt:1: needle here", "b.txt:1: needle here", "c.txt:1: needle here"])
        self.assertNotIn("SECRET", hits)
        with patch.object(orchestrator, "CODE_MAX_ENTRIES", 2), patch.object(orchestrator, "CODE_MAX_HITS", 2), \
                patch.object(orchestrator, "CODE_MAX_READ", 4):
            self.assertIn("truncated at 2 entries", tools["list_files"]())
            self.assertEqual(len(tools["search_files"]("needle").splitlines()), 3)
            self.assertTrue(tools["read_file"]("a.txt").startswith("need\n... truncated at 4 bytes"))
        with self.assertRaisesRegex(orchestrator.WorkflowError, "outside"):
            tools["list_files"](str(self.cfg))

    def test_walks_stay_inside_grants(self):
        (self.cfg / "secret.txt").write_text("needle OUTSIDE\n", encoding="utf-8")
        (self.root / "a.txt").write_text("needle inside\n", encoding="utf-8")
        link = self.root / "link"
        if subprocess.run(["cmd", "/c", "mklink", "/J", str(link), str(self.cfg)], capture_output=True).returncode != 0:
            self.skipTest("junctions unavailable")
        tools = orchestrator.code_handlers(self.folders)
        self.assertNotIn("link", tools["list_files"]())
        self.assertEqual(tools["search_files"]("needle"), "a.txt:1: needle inside")
        sibling = Path(self.folders[0]["path"] + "2")  # Y:\app2 must not pass as inside Y:\app
        with self.assertRaisesRegex(orchestrator.WorkflowError, "outside"):
            orchestrator.code_path(str(sibling / "x"), self.folders)

    def test_project_folder_is_always_writable(self):
        folders = [{"path": str(self.root), "access": "read"}]
        replies = {"planner": [tool_call("write_file", path="a.txt", content="x"), "Done."]}
        code, *_ = self.code({"action": "chat", "messages": self.MSGS, "folders": folders}, replies)
        self.assertEqual((code, (self.root / "a.txt").read_text(encoding="utf-8")), (0, "x\n"))

    def test_chat_turn_writes_file_and_reports(self):
        (self.shared / "lib.py").write_text("x = 1\n", encoding="utf-8")
        replies = {"planner": [tool_call("write_file", path="README.md", content="# App"),
                               tool_call("write_file", path=str(self.shared / "lib.py"), content="hacked"),
                               tool_call("replace_text", path="README.md", old="App", new="My app"),
                               "<think>ok</think>Added README.md."]}
        code, log, events, unloaded = self.code({"action": "chat", "messages": self.MSGS}, replies)
        self.assertEqual((code, unloaded), (0, []))
        self.assertEqual((self.root / "README.md").read_text(encoding="utf-8"), "# My app\n")
        self.assertEqual((self.shared / "lib.py").read_text(encoding="utf-8"), "x = 1\n")
        tools = [(d["name"], d["result"].split(":")[0]) for e, d in events if e == "tool"]
        self.assertEqual(tools, [("write_file", "OK"), ("write_file", "ERROR"), ("replace_text", "OK")])
        self.assertEqual([d for e, d in events if e == "code_reply"], [{"role": "planner", "text": "Added README.md."}])
        self.assertIn("FOLDERS", log[0]["system"])
        self.assertIn(f"{self.shared} (read)", log[0]["system"])
        self.assertEqual({t["function"]["name"] for t in log[0]["tools"]},
                         {"list_files", "read_file", "search_files", "write_file", "replace_text", "write_project_md", "start_project",
                          "todo", "run_checks", "delegate"})

    def test_start_project_emits_code_start(self):
        (self.root / "PROJECT.md").write_text("# Project\n", encoding="utf-8")
        replies = {"planner": [tool_call("start_project"), "Starting the project."]}
        code, log, events, _ = self.code({"action": "chat", "worker": "planner", "messages": self.MSGS}, replies)
        self.assertEqual([e for e, _ in events if e.startswith("code_")], ["code_start", "code_reply"])
        self.assertIn("OK: the project run will start", log[1]["messages"][-1]["content"])

    def test_start_project_without_project_md_is_refused(self):
        replies = {"planner": [tool_call("start_project"), "Please add a PROJECT.md."]}
        code, log, events, _ = self.code({"action": "chat", "worker": "planner", "messages": self.MSGS}, replies)
        self.assertEqual([e for e, _ in events if e.startswith("code_")], ["code_reply"])
        self.assertIn("ERROR: the project root has no PROJECT.md", log[1]["messages"][-1]["content"])

    PROJECT = "# Project\n\n## Project Goal\n\nA todo app.\n"

    def test_write_project_writes_backs_up_and_rejects_empty(self):
        code, log, events, _ = self.code({"action": "write_project", "worker": "planner", "messages": []},
                                         {"planner": [tool_call("list_files"), f"```markdown\n{self.PROJECT}```"]})
        self.assertEqual(code, 0)
        self.assertEqual((self.root / "PROJECT.md").read_text(encoding="utf-8"), self.PROJECT)
        self.assertEqual({t["function"]["name"] for t in log[0]["tools"]}, {"list_files", "read_file", "search_files"})
        self.assertIn("[Describe the project", log[0]["messages"][0]["content"])  # the workflow root's template
        self.assertEqual([(e, d.get("path") or d.get("text")) for e, d in events if e.startswith("code_")],
                         [("code_project", str(self.root / "PROJECT.md")), ("code_reply", "PROJECT.md written: A todo app.")])
        code, log, *_ = self.code({"action": "write_project", "worker": "planner", "messages": self.MSGS},
                                  {"planner": ["# Project\n\n## Project Goal\n\nA better todo app.\n"]})
        self.assertIn("CURRENT PROJECT.md\n# Project", log[0]["messages"][0]["content"])
        backups = list(self.root.glob("PROJECT.backup-*.md"))
        self.assertEqual([b.read_text(encoding="utf-8") for b in backups], [self.PROJECT])
        for bad in ("", "Just prose, no headings."):
            with self.subTest(bad), self.assertRaisesRegex(orchestrator.WorkflowError, "did not return a PROJECT.md"):
                self.code({"action": "write_project", "worker": "planner", "messages": []}, {"planner": [bad]})
        self.assertIn("better", (self.root / "PROJECT.md").read_text(encoding="utf-8"))
        self.assertEqual(len(list(self.root.glob("PROJECT.backup-*.md"))), 1)

    def test_write_project_md_tool_uses_implementer(self):
        replies = {"planner": [tool_call("write_project_md", notes="todo app"), "Wrote it."],
                   "implementer": [self.PROJECT]}
        code, log, events, _ = self.code({"action": "chat", "messages": self.MSGS}, replies)
        self.assertEqual([l["role"] for l in log], ["planner", "implementer", "planner"])
        self.assertIn("NOTES\ntodo app", log[1]["messages"][0]["content"])
        self.assertEqual([d["result"] for e, d in events if e == "tool"], ["OK: PROJECT.md written (5 lines)"])
        self.assertEqual([e for e, _ in events if e.startswith("code_")], ["code_project", "code_reply"])

    def test_chat_and_code_are_mutually_exclusive(self):
        err = io.StringIO()
        err.reconfigure = lambda **kw: None
        with patch.object(orchestrator.sys, "argv", ["o", "--chat", "a.json", "--code", "b.json"]), \
                patch.object(orchestrator.sys, "stderr", err), self.assertRaises(SystemExit):
            orchestrator._run()
        self.assertIn("not allowed with argument", err.getvalue())


class LeadTests(unittest.TestCase):
    """A Code chat turn's lead agent (planner): todo, run_checks and delegate to fresh implementer/reviewer sub-agents."""

    setUp, code, MSGS = CodeTests.setUp, CodeTests.code, CodeTests.MSGS
    names = staticmethod(lambda entry: [t["function"]["name"] for t in entry["tools"]])

    def test_lead_delegates_an_edit_then_a_review_that_runs_the_checks(self):
        replies = {"planner": [tool_call("todo", items=["[ ] add app.py"]), tool_call("delegate", worker="implementer", brief="Write app.py: x = 1"),
                               tool_call("delegate", worker="reviewer", brief="Check app.py"), "Added app.py; the review found no problems."],
                   "implementer": [tool_call("write_file", path="app.py", content="x = 1"), "Wrote app.py."],
                   "reviewer": [tool_call("run_checks"), "No problems: the syntax check passed."]}
        code, log, events, _ = self.code({"action": "chat", "messages": self.MSGS}, replies)
        self.assertEqual(code, 0)
        self.assertEqual((self.root / "app.py").read_text(encoding="utf-8"), "x = 1\n")
        self.assertEqual([l["role"] for l in log], ["planner"] * 2 + ["implementer"] * 2 + ["planner"] + ["reviewer"] * 2 + ["planner"])
        code_tools = [t["function"]["name"] for t in orchestrator.CODE_TOOLS]
        self.assertEqual(self.names(log[0]), code_tools + ["todo", "run_checks", "delegate"])
        self.assertEqual(self.names(log[2]), code_tools[:5])  # implementer: file tools, no delegate
        self.assertEqual(self.names(log[5]), code_tools[:3] + ["run_checks"])  # reviewer: read tools + run_checks
        for sub, brief in ((log[2], "Write app.py: x = 1"), (log[5], "Check app.py")):
            self.assertEqual(sub["messages"], [{"role": "user", "content": brief}])  # fresh context: the brief only
            self.assertIn("FOLDERS", sub["system"])
        self.assertIn("implementer working for a lead agent", log[2]["system"])
        self.assertIn("reviewer working for a lead agent", log[5]["system"])
        results = [(d["role"], d["name"], d["result"]) for e, d in events if e == "tool"]
        self.assertEqual(results, [("planner", "todo", "CHECKLIST\n[ ] add app.py"), ("implementer", "write_file", results[1][2]),
                                   ("planner", "delegate", "Wrote app.py."), ("reviewer", "run_checks", "SYNTAX CHECK: passed (app.py)"),
                                   ("planner", "delegate", "No problems: the syntax check passed.")])
        self.assertTrue(results[1][2].startswith("OK"))
        self.assertEqual([(d["role"], d["stage"]) for e, d in events if e == "chat_stage"],
                         [("planner", "lead"), ("implementer", "delegate"), ("planner", "lead"), ("reviewer", "delegate"), ("planner", "lead")])
        self.assertEqual([d for e, d in events if e == "code_reply"], [{"role": "planner", "text": "Added app.py; the review found no problems."}])

    def test_max_delegations_caps_the_turn(self):
        self.config["max_delegations"] = 1
        replies = {"planner": [tool_call("delegate", worker="implementer", brief="a"), tool_call("delegate", worker="implementer", brief="b"), "FINAL"],
                   "implementer": ["R1"]}
        code, log, events, _ = self.code({"action": "chat", "messages": self.MSGS}, replies)
        self.assertEqual(code, 0)
        self.assertEqual([d["result"] for e, d in events if e == "tool"],
                         ["R1", "ERROR: delegation limit reached (1 per turn); finish the work yourself"])
        self.assertEqual([l["role"] for l in log], ["planner", "implementer", "planner", "planner"])

    def test_a_sub_agent_cannot_delegate(self):
        replies = {"planner": [tool_call("delegate", worker="reviewer", brief="check"), "FINAL"],
                   "reviewer": [tool_call("delegate", worker="implementer", brief="nested"), "REVIEW"]}
        code, log, events, _ = self.code({"action": "chat", "messages": self.MSGS}, replies)
        self.assertEqual(code, 0)
        self.assertNotIn("delegate", self.names(log[1]))
        self.assertEqual([d["result"] for e, d in events if e == "tool"], ["ERROR: tool 'delegate' is unavailable", "REVIEW"])
        self.assertNotIn("implementer", [l["role"] for l in log])

    def test_run_checks_without_checks_and_with_a_python_syntax_error(self):
        (self.root / "notes.txt").write_text("x", encoding="utf-8")
        self.assertTrue(orchestrator.run_checks(self.config, self.folders).startswith("No checks apply here"))
        (self.root / "bad.py").write_text("def f(:\n", encoding="utf-8")
        result = orchestrator.run_checks(self.config, self.folders)
        self.assertTrue(result.startswith("SYNTAX CHECK: FAILED (bad.py)\n- bad.py: SyntaxError: "), result)
        self.assertIn("(line 1)", result)

    def test_run_checks_returns_validation_output_clipped(self):
        noisy = "import sys; print('x' * 2000); print('boom at x.py:3'); sys.exit(1)"
        self.config["validation_commands"] = {"a": [orchestrator.sys.executable, "-c", noisy], "b": [orchestrator.sys.executable, "-c", noisy]}
        result = orchestrator.run_checks(self.config, self.folders)
        self.assertTrue(result.startswith("VALIDATION a: exit_code=1\n"), result[:100])
        self.assertIn("boom at x.py:3", result)
        self.assertLessEqual(len(result), orchestrator.CHECKS_LIMIT + len(" ...[truncated]"))
        self.assertTrue(result.endswith("...[truncated]"))

    def test_legacy_mode_and_worker_are_ignored(self):
        for legacy in ({"mode": "multi", "worker": "reviewer"}, {"mode": "single", "worker": "implementer"}, {"mode": "bogus"}):
            code, log, events, _ = self.code({"action": "chat", "messages": self.MSGS, **legacy}, {"planner": ["done"]})
            self.assertEqual((code, [l["role"] for l in log]), (0, ["planner"]), legacy)
            self.assertEqual([d for e, d in events if e == "code_reply"], [{"role": "planner", "text": "done"}])


class LeadRunTests(unittest.TestCase):
    """Start project (the default run): the lead works through PROJECT.md, records progress in STATE.md and finishes."""

    STATE = "# Verified state\n\nNo tasks have passed yet.\n"

    def setUp(self):
        tmp = tempfile.TemporaryDirectory()
        self.addCleanup(tmp.cleanup)
        self.root = Path(tmp.name).resolve()
        (self.root / "PROJECT.md").write_text("# Project\n\n## Project Goal\n\nA greeting script.\n", encoding="utf-8")
        (self.root / "STATE.md").write_text(self.STATE, encoding="utf-8")
        self.config = {"roles": {r: {"base_url": "http://x", "expected_model_contains": f"{r}-model"} for r in orchestrator.BUILTIN_ROLES}}

    def run_project(self, replies, once=False):
        replies = {k: list(v) for k, v in replies.items()}
        log, events = [], []

        class Fake:
            def __init__(self, settings):
                self.settings, self.role, self.title = settings, "", ""

            def ensure_model(self, role, interactive):
                return "m"

            def complete(self, model, messages, sampling, tools=None):
                log.append({"role": self.role, "system": messages[0]["content"], "messages": list(messages[1:]), "tools": tools})
                reply = replies[self.role].pop(0)
                return {"role": "assistant", "content": reply} if isinstance(reply, str) else reply

        (self.root / "config.json").write_text(json.dumps(self.config), encoding="utf-8")
        argv = ["o", "--project", str(self.root), "--non-interactive", "--events", *(["--once"] if once else [])]
        with patch.object(orchestrator, "ApiClient", Fake), patch.object(orchestrator, "emit", lambda e, **d: events.append((e, d))), \
                patch.object(orchestrator, "unload_models", lambda: None), patch.object(orchestrator.sys, "argv", argv):
            code = orchestrator.main()
        return code, log, events

    def text(self, name):
        return (self.root / name).read_text(encoding="utf-8")

    def test_runs_to_finish_and_records_progress(self):
        replies = {"planner": [tool_call("todo", items=["[ ] greeting"]), tool_call("write_file", path="hello.py", content="print('hi')"),
                               tool_call("run_checks"), tool_call("record_progress", item="Greeting script", outcome="hello.py prints hi"),
                               tool_call("finish", summary="Greeting script done")]}
        code, log, events = self.run_project(replies)
        self.assertEqual((code, len(log)), (0, 5))
        self.assertTrue(log[0]["system"].startswith((Path(orchestrator.ROOT) / "prompts" / "lead_project.txt").read_text(encoding="utf-8").strip()))
        self.assertEqual([t["function"]["name"] for t in log[0]["tools"]],
                         [t["function"]["name"] for t in orchestrator.CODE_TOOLS[:5]] + ["todo", "run_checks", "delegate", "record_progress", "finish"])
        state = self.text("STATE.md")
        self.assertRegex(state, r"\n## Done: Greeting script\n- Time: .+\n- Outcome: hello.py prints hi\n")
        self.assertRegex(state, r"\n## Project complete\n- Time: .+\n- Summary: Greeting script done\n$")
        tools = [(d["name"], d["result"]) for e, d in events if e == "tool"]
        self.assertIn(("run_checks", "SYNTAX CHECK: passed (hello.py)"), tools)
        self.assertEqual([(e, d.get("id")) for e, d in events if e in ("task", "complete")], [("task", "Greeting script"), ("complete", None)])

    def test_a_reply_without_tool_calls_is_nudged_and_resume_input_has_the_recorded_items(self):
        (self.root / "STATE.md").write_text(self.STATE + "\n## Done: Item A\n- Outcome: works\n", encoding="utf-8")
        code, log, _ = self.run_project({"planner": ["I will continue.", tool_call("finish", summary="All done")]})
        self.assertEqual(code, 0)
        task = log[0]["messages"][0]["content"]
        for part in ("PROJECT.md\n# Project", "## Done: Item A", "FILES (project root)\n", "STATE.md"):
            self.assertIn(part, task)
        self.assertEqual(log[1]["messages"][-1], {"role": "user", "content": orchestrator.LEAD_NUDGE})

    def test_once_stops_after_the_first_recorded_item(self):
        replies = {"planner": [tool_call("record_progress", item="First", outcome="ok"), tool_call("record_progress", item="Second", outcome="ok")]}
        code, log, _ = self.run_project(replies, once=True)
        self.assertEqual((code, len(log)), (0, 1))
        self.assertIn("## Done: First", self.text("STATE.md"))
        self.assertNotIn("Second", self.text("STATE.md"))
        self.assertIn("Last done: First", self.text("HANDOFF.md"))

    def test_step_cap_stops_with_an_error(self):
        self.config["max_lead_steps"] = 2
        with self.assertRaisesRegex(orchestrator.WorkflowError, "exceeded max_lead_steps=2"):
            self.run_project({"planner": ["thinking", "still thinking"]})  # __main__ prints it as an ERROR line, exit code 1
        self.assertNotIn("## Done", self.text("STATE.md"))

    def test_refuses_to_write_the_orchestrator_folder(self):
        with self.assertRaisesRegex(orchestrator.WorkflowError, "overlaps the Agent Orchestrator folder"):
            orchestrator.run_lead_project(self.config, orchestrator.ROOT, False)

    def test_delegation_inside_the_run(self):
        replies = {"planner": [tool_call("delegate", worker="implementer", brief="Write hello.py"),
                               tool_call("record_progress", item="Greeting", outcome="delegated"), tool_call("finish", summary="done")],
                   "implementer": [tool_call("write_file", path="hello.py", content="print('hi')"), "Wrote hello.py."]}
        code, log, events = self.run_project(replies)
        self.assertEqual(code, 0)
        self.assertEqual(self.text("hello.py"), "print('hi')\n")
        self.assertEqual([l["role"] for l in log], ["planner", "implementer", "implementer", "planner", "planner"])
        self.assertEqual(log[1]["messages"], [{"role": "user", "content": "Write hello.py"}])
        self.assertIn(("planner", "delegate", "Wrote hello.py."), [(d["role"], d["name"], d["result"]) for e, d in events if e == "tool"])


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
        argv = ["o", "--project", str(root), "--non-interactive", "--pipeline"]
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

    def test_scaffold_loads_js_as_modules_when_the_contract_says_so(self):
        for contract in ({"files": [{"path": "index.html", "loads": ["app.js"]}], "notes": ["Scripts are ES Modules."]},
                         {"files": [{"path": "index.html", "loads": ["app.js"]}, {"path": "app.js", "purpose": "Imports util.js"}]}):
            with tempfile.TemporaryDirectory(dir=Path(__file__).parent) as folder, contextlib.redirect_stdout(io.StringIO()):
                orchestrator.scaffold_contract({}, Path(folder), contract)
                page = (Path(folder) / "index.html").read_text(encoding="utf-8")
            self.assertIn('<script type="module" src="app.js"></script>', page, contract)

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

    def test_remote_role_refuses_the_local_unsloth_key(self):
        sent = []
        for url, refused in ((self.URL, True), ("http://localhost:9000/v1", False), ("http://[::1]:9000/v1", False)):
            role = dict(self.CONFIG["roles"]["planner"], base_url=url, api_key_env="UNSLOTH_API_KEY")
            config = {"roles": {"planner": role}}
            with patch.dict(os.environ, {"UNSLOTH_API_KEY": self.KEY}), patch.object(orchestrator, "urlopen", side_effect=lambda r, timeout=None: sent.append(r)):
                if refused:
                    with self.assertRaises(orchestrator.WorkflowError) as ctx:
                        orchestrator.role_settings(config, "planner")
                    self.assertTrue(str(ctx.exception).startswith("planner: "), ctx.exception)
                    self.assertIn("UNSLOTH_API_KEY", str(ctx.exception))
                    self.assertNotIn(self.KEY, str(ctx.exception))
                else:
                    orchestrator.role_settings(config, "planner")  # a local server without Unsloth management is fine
        self.assertEqual(sent, [])

    def test_remote_effort_keeps_reasoning_effort_and_drops_enable_thinking(self):
        sent = []

        def fake(request, timeout=None):
            sent.append(json.loads(request.data))
            return self.response({"choices": [{"message": {"content": "{}"}, "finish_reason": "stop"}], "usage": {}})

        config = orchestrator.with_effort(self.CONFIG, {"effort": "high"})
        with patch.dict(os.environ, {"REMOTE_TEST_KEY": self.KEY}), patch.object(orchestrator, "EVENTS", False), \
                patch.object(orchestrator, "urlopen", side_effect=fake), contextlib.redirect_stdout(io.StringIO()):
            orchestrator.ask_once(config, "planner", "sys", "user", False)
        self.assertNotIn("enable_thinking", sent[0])
        self.assertEqual(sent[0]["reasoning_effort"], "high")

    def test_remote_body_omits_unsloth_only_fields_unless_send_extra_body(self):
        sent = []

        def fake(request, timeout=None):
            sent.append(json.loads(request.data))
            return self.response({"choices": [{"message": {"content": "{}"}, "finish_reason": "stop"}], "usage": {}})

        extra = {"top_k": 20, "repetition_penalty": 1.05, "min_p": 0.1, "chat_template_kwargs": {"x": 1}}
        dropped = set(extra) | {"enable_thinking"}
        role = dict(self.CONFIG["roles"]["planner"], sampling={"temperature": 0.2, "max_tokens": 64, "extra_body": extra})
        for send_extra in (False, True):
            sent.clear()
            config = {"roles": {"planner": dict(role, send_extra_body=True) if send_extra else role}}
            with patch.dict(os.environ, {"REMOTE_TEST_KEY": self.KEY}), patch.object(orchestrator, "EVENTS", False), \
                    patch.object(orchestrator, "urlopen", side_effect=fake), contextlib.redirect_stdout(io.StringIO()):
                orchestrator.ask_once(config, "planner", "sys", "user", False)
                orchestrator.ask_once(config, "planner", "sys", "user", False, thinking=False)  # the JSON-repair call
            self.assertEqual(len(sent), 2)
            for body in sent:
                self.assertNotIn("enable_thinking", body)  # never added for a remote role
                self.assertEqual(set(extra) <= set(body), send_extra, body)
                self.assertEqual(body["temperature"], 0.2)

        # cut off while thinking: no enable_thinking retry for a remote role, and no auto_load advice
        sent.clear()
        cut = lambda request, timeout=None: (sent.append(json.loads(request.data)), self.response(
            {"choices": [{"message": {"content": ""}, "finish_reason": "length"}], "usage": {}}))[1]
        with patch.dict(os.environ, {"REMOTE_TEST_KEY": self.KEY}), patch.object(orchestrator, "EVENTS", False), \
                patch.object(orchestrator, "urlopen", side_effect=cut), contextlib.redirect_stdout(io.StringIO()):
            with self.assertRaises(orchestrator.WorkflowError) as ctx:
                orchestrator.ask_once({"roles": {"planner": role}}, "planner", "sys", "user", False)
        self.assertEqual(len(sent), 1)
        self.assertFalse(dropped & set(sent[0]))
        self.assertNotIn("auto_load", str(ctx.exception))

    def test_remote_context_overflow_does_not_suggest_auto_load(self):
        from urllib.error import HTTPError

        def fail(request, timeout=None):
            raise HTTPError(request.full_url, 400, "err", {}, io.BytesIO(b'{"error": "context length exceeded"}'))

        with patch.dict(os.environ, {"REMOTE_TEST_KEY": self.KEY}), patch.object(orchestrator, "EVENTS", False), \
                patch.object(orchestrator, "urlopen", side_effect=fail), contextlib.redirect_stdout(io.StringIO()):
            with self.assertRaises(orchestrator.WorkflowError) as ctx:
                orchestrator.ask_once(self.CONFIG, "planner", "sys", "user", False)
        self.assertIn("exceeded the model's context length", str(ctx.exception))
        self.assertNotIn("auto_load", str(ctx.exception))

    def test_custom_worker_with_only_a_model_id_is_valid(self):
        worker = {"id": "w", "title": "Remote", "kind": "reviewer", "system_prompt": "Review.", "base_url": self.URL, "model": "vendor/model-x"}
        self.assertEqual([w["id"] for w in orchestrator.custom_workers({"roles": {}, "custom_workers": [worker]})], ["w"])


class ExtrasTests(unittest.TestCase):
    """Search, utilities and skills for Chat and Code turns. No network: web_request and DNS are patched."""

    DDG = ('<div><a rel="nofollow" class="result__a" href="//duckduckgo.com/l/?uddg=https%3A%2F%2Fpeony.org%2Fcare&amp;rut=x">Peony <b>care</b></a>'
           '<a class="result__snippet" href="x">Full <b>sun</b> &amp; well-drained soil.</a></div>'
           '<div><a class="result__a" href="https://duckduckgo.com/y.js?ad=1">Buy now</a></div>'
           '<div><a href="https://example.org/b" class="result__a">Second</a></div>')

    def test_calculator_and_time(self):
        self.assertEqual(orchestrator.calculator("2**10 + 3*(4-1)"), "1033")
        self.assertEqual(orchestrator.calculator("round(sqrt(2), 3)"), "1.414")
        for bad in ('__import__("os").system("x")', "open('f')", "9**9**9", "((9**1000)**1000)**1000", "(2**1000)**200", "1/0", "", "a.b"):
            with self.subTest(bad), self.assertRaises(orchestrator.WorkflowError):
                orchestrator.calculator(bad)
        self.assertRegex(orchestrator.current_time(), r"^\w+day \d{4}-\d\d-\d\d \d\d:\d\d:\d\d ")

    def test_web_search_parses_results_and_skips_ads(self):
        sent = []
        with patch.object(orchestrator, "web_request", lambda url, data=None: (sent.append((url, data)), (self.DDG, "text/html"))[1]):
            out = orchestrator.web_search("peony care")
        self.assertEqual(sent, [("https://html.duckduckgo.com/html/", b"q=peony+care")])
        self.assertEqual(out, "1. Peony care\n   https://peony.org/care\n   Full sun & well-drained soil.\n2. Second\n   https://example.org/b")
        with patch.object(orchestrator, "web_request", lambda url, data=None: ("<html>blocked</html>", "text/html")):
            self.assertIn("no results", orchestrator.web_search("x"))
        with self.assertRaises(orchestrator.WorkflowError):
            orchestrator.web_search("  ")

    def test_fetch_page_reads_public_text_only(self):
        public = [(2, 1, 6, "", ("93.184.216.34", 0))]
        private = [(2, 1, 6, "", ("10.0.0.5", 0))]
        page = "<html><title>T &amp; C</title><script>x()</script><body><p>Hello</p>world</body></html>"
        with patch.object(orchestrator.socket, "getaddrinfo", lambda host, port: public), \
                patch.object(orchestrator, "web_request", lambda url, data=None: (page, "text/html")):
            self.assertEqual(orchestrator.fetch_page("https://example.com/a"), "TITLE: T & C\n\nT & C Hello\nworld")
        with patch.object(orchestrator.socket, "getaddrinfo", lambda host, port: public), \
                patch.object(orchestrator, "web_request", lambda url, data=None: ("x" * 30_000, "text/plain")):
            self.assertTrue(orchestrator.fetch_page("http://example.com/").endswith("truncated at 20000 characters"))
        with patch.object(orchestrator.socket, "getaddrinfo", lambda host, port: public), \
                patch.object(orchestrator, "web_request", lambda url, data=None: ("%PDF", "application/pdf")), \
                self.assertRaisesRegex(orchestrator.WorkflowError, "not a text page"):
            orchestrator.fetch_page("https://example.com/f.pdf")
        fetched = []
        with patch.object(orchestrator.socket, "getaddrinfo", lambda host, port: private), \
                patch.object(orchestrator, "web_request", lambda url, data=None: fetched.append(url)):
            for url in ("http://intranet/", "file:///C:/x", "ftp://example.com/", "https:///nohost"):
                with self.subTest(url), self.assertRaises(orchestrator.WorkflowError):
                    orchestrator.fetch_page(url)
        self.assertEqual(fetched, [])

    def test_redirects_only_to_public_hosts_and_bad_charsets_fall_back(self):
        request = orchestrator.Request("https://example.com/r")
        with patch.object(orchestrator.socket, "getaddrinfo", lambda host, port: [(2, 1, 6, "", ("127.0.0.1", 0))]), \
                self.assertRaisesRegex(orchestrator.WorkflowError, "local or private"):
            orchestrator.PublicRedirects().redirect_request(request, None, 302, "Found", {}, "http://localhost:8888/v1/models")
        with patch.object(orchestrator.socket, "getaddrinfo", lambda host, port: [(2, 1, 6, "", ("93.184.216.34", 0))]):
            self.assertEqual(orchestrator.PublicRedirects().redirect_request(request, None, 302, "Found", {}, "https://example.org/b").full_url,
                             "https://example.org/b")
        self.assertEqual((orchestrator.web_charset("bogus"), orchestrator.web_charset(None), orchestrator.web_charset("latin-1")),
                         ("utf-8", "utf-8", "iso8859-1"))

    def test_request_extras_switches_and_skills(self):
        names = lambda request: [t["function"]["name"] for t in orchestrator.request_extras(request)[0]]
        self.assertEqual(orchestrator.request_extras({}), ([], {}, ""))
        self.assertEqual(names({"search": True}), ["web_search", "fetch_page"])
        self.assertEqual(names({"tools": True}), ["calculator", "current_time", "fetch_page"])
        self.assertEqual(names({"search": True, "tools": True}), ["web_search", "fetch_page", "calculator", "current_time"])
        with tempfile.TemporaryDirectory() as folder:
            skill = Path(folder) / "garden"
            skill.mkdir()
            (skill / "SKILL.md").write_text("---\nname: gardening\ndescription: Plant care steps\n---\n# Steps\nWater weekly.\n", encoding="utf-8")
            (skill / "zones.md").write_text("Zone 3-8", encoding="utf-8")
            (Path(folder) / "secret.txt").write_text("no", encoding="utf-8")
            bare = Path(folder) / "bare"
            bare.mkdir()
            (bare / "SKILL.md").write_text("Just text", encoding="utf-8")
            bom = Path(folder) / "bom"
            bom.mkdir()
            (bom / "SKILL.md").write_text("---\nname: noted\ndescription: Saved with a BOM\n---\n", encoding="utf-8-sig")
            tools, handlers, system = orchestrator.request_extras({"skills": [str(skill), str(bare), str(bom), str(Path(folder) / "missing"), "relative"]})
            self.assertEqual([t["function"]["name"] for t in tools], ["load_skill"])
            self.assertIn("SKILLS\n", system)
            self.assertIn("- gardening: Plant care steps\n- bare: (no description)\n- noted: Saved with a BOM", system)
            self.assertIn("Water weekly.", handlers["load_skill"]("gardening"))
            self.assertEqual(handlers["load_skill"]("gardening", "zones.md"), "Zone 3-8")
            for args in (("gardening", "../secret.txt"), ("gardening", str(Path(folder) / "secret.txt")), ("nobody",), ("gardening", "none.md")):
                with self.subTest(args), self.assertRaises(orchestrator.WorkflowError):
                    handlers["load_skill"](*args)

    def test_chat_with_search_runs_tool_loop(self):
        call = {"role": "assistant", "content": "", "tool_calls": [
            {"id": "c1", "type": "function", "function": {"name": "web_search", "arguments": "{\"query\": \"peony care\"}"}}]}
        with patch.object(orchestrator, "web_request", lambda url, data=None: (self.DDG, "text/html")):
            code, log, events, *_ = ChatTests().chat({"mode": "single", "worker": "implementer", "messages": ChatTests.MSGS, "search": True},
                                                     {"implementer": [call, "Plant in full sun [1](https://peony.org/care)"]})
        self.assertEqual(code, 0)
        self.assertEqual([t["function"]["name"] for t in log[0]["tools"]], ["web_search", "fetch_page", "delegate"])
        self.assertIn("WEB SEARCH\n", log[0]["system"])
        tool = [d for e, d in events if e == "tool"]
        self.assertEqual((tool[0]["name"], "https://peony.org/care" in tool[0]["result"]), ("web_search", True))
        self.assertEqual(events[-1], ("chat_reply", {"role": "implementer", "text": "Plant in full sun [1](https://peony.org/care)"}))

    def test_first_turn_asks_the_answering_model_for_a_short_title(self):
        request = {"mode": "single", "worker": "implementer", "messages": ChatTests.MSGS, "title": True}
        code, log, events, *_ = ChatTests().chat(request, {"implementer": ["the answer", '"Windows Calculator Batch File."\nmore']})
        self.assertEqual(code, 0)
        self.assertEqual(len(log), 2)
        self.assertIn("hi", log[1]["input"])  # the first user message, and the reply, go into the title prompt
        self.assertIn("the answer", log[1]["input"])
        self.assertEqual((log[1]["sampling"]["max_tokens"], log[1]["sampling"]["enable_thinking"]), (40, False))
        self.assertEqual(events[-2:], [("chat_reply", {"role": "implementer", "text": "the answer"}),
                                       ("chat_title", {"text": "Windows Calculator Batch File"})])
        # later turns ask for no title; an empty title is not sent
        code, log, events, *_ = ChatTests().chat({**request, "title": False}, {"implementer": ["the answer"]})
        self.assertEqual((len(log), [e for e, _ in events if e == "chat_title"]), (1, []))
        code, log, events, *_ = ChatTests().chat(request, {"implementer": ["the answer", "  "]})
        self.assertEqual([e for e, _ in events if e == "chat_title"], [])

    def test_chat_and_code_get_the_extras(self):
        code, log, *_ = ChatTests().chat({"messages": ChatTests.MSGS, "tools": True})
        self.assertEqual(code, 0)
        self.assertEqual([[t["function"]["name"] for t in entry["tools"]] for entry in log], [["calculator", "current_time", "fetch_page", "delegate"]])
        test = CodeTests("test_request_validation")
        test.setUp()
        try:
            code, log, *_ = test.code({"action": "chat", "messages": CodeTests.MSGS, "tools": True}, {"planner": ["done"]})
        finally:
            test.doCleanups()
        names = [t["function"]["name"] for t in log[0]["tools"]]
        self.assertEqual(names, [t["function"]["name"] for t in orchestrator.CODE_TOOLS]
                         + ["todo", "run_checks", "delegate", "calculator", "current_time", "fetch_page"])
        self.assertIn("UTILITIES\n", log[0]["system"])


class LlamaCppTests(unittest.TestCase):
    """Local roles on our own llama-server (AGENT_ORCHESTRATOR_BACKEND=llamacpp); no network and no real process."""

    PATH = "unsloth/Qwen3.5-4B-GGUF"
    SNAP = "hub/models--unsloth--Qwen3.5-4B-GGUF/snapshots/abc/"
    AUTO = {"model_path": PATH, "gguf_variant": "Q4_K_M", "max_seq_length": 32768, "cache_type_kv": "q8_0", "n_batch": 1024,
            "n_ubatch": 256, "gpu_layers": -1, "reasoning_budget": 0, "llama_extra_args": ["--flash-attn", "on"]}
    ROLE = {"base_url": "http://127.0.0.1:8888/v1", "management_url": "http://127.0.0.1:8888", "api_key_env": "UNSLOTH_API_KEY",
            "expected_model_contains": "Qwen3.5-4B-GGUF", "timeout_seconds": 30, "auto_load": AUTO}

    def setUp(self):
        self.tmp = Path(tempfile.mkdtemp(dir=Path(__file__).parent))
        self.addCleanup(shutil.rmtree, self.tmp, True)
        (self.tmp / "temp").mkdir()
        self.exe = self.tmp / "llama-server.exe"
        self.exe.write_bytes(b"")
        self.enterContext(patch.dict(os.environ, {"AGENT_ORCHESTRATOR_BACKEND": "llamacpp", "AGENT_ORCHESTRATOR_LLAMA_SERVER": str(self.exe),
                                                  "AGENT_ORCHESTRATOR_GGUF_DIRS": "", "HF_HUB_CACHE": str(self.tmp / "hub")}))
        os.environ.pop("UNSLOTH_API_KEY", None)  # restored by patch.dict; llama.cpp roles need no key
        self.enterContext(patch.object(tempfile, "tempdir", str(self.tmp / "temp")))
        self.enterContext(patch.object(orchestrator.time, "sleep"))
        self.enterContext(contextlib.redirect_stdout(io.StringIO()))
        self.emit = self.enterContext(patch.object(orchestrator, "emit"))
        self.enterContext(patch.object(orchestrator, "EVENTS", False))  # chat requests go through _request, not the stream
        self.loaded = self.enterContext(patch.object(orchestrator, "LOADED_BY_RUN", {}))
        self.alive = set()  # pids llama_alive reports as running llama-servers; os.kill removes them
        self.enterContext(patch.object(orchestrator, "llama_alive", side_effect=lambda pid: pid in self.alive))
        self.kill = self.enterContext(patch.object(orchestrator.os, "kill", side_effect=lambda pid, sig: self.alive.discard(pid)))
        self.healthy = self.enterContext(patch.object(orchestrator, "llama_healthy", return_value=True))
        self.popen = self.enterContext(patch.object(orchestrator.subprocess, "Popen"))
        self.popen.return_value.pid = 77
        self.popen.return_value.poll.return_value = None

    def touch(self, *names):
        for name in names:
            (self.tmp / name).parent.mkdir(parents=True, exist_ok=True)
            (self.tmp / name).write_bytes(b"")
        return self.tmp / names[-1]

    def find(self, **auto):
        return orchestrator.gguf_file({"model_path": self.PATH, **auto})

    def state_file(self):
        return self.tmp / "temp" / "agent-orchestrator-llama.json"

    def write_state(self, pid, signature):
        self.state_file().write_text(json.dumps({"pid": pid, "port": 8899, "signature": signature, "model_file": "x", "log": "y"}))

    def client(self, config=None):
        client, _ = orchestrator.role_settings(config or {"roles": {"planner": self.ROLE}}, "planner")
        client.model_id = lambda: self.PATH
        return client

    def test_gguf_prefers_the_variant(self):
        self.touch(self.SNAP + "Qwen3.5-4B-BF16.gguf")
        wanted = self.touch(self.SNAP + "Qwen3.5-4B-Q4_K_M.gguf")
        self.assertEqual(self.find(gguf_variant="q4_k_m"), wanted)

    def test_gguf_skips_the_vision_projector(self):
        self.touch(self.SNAP + "mmproj-f16.gguf")
        wanted = self.touch(self.SNAP + "qwen3.5-4b-q4_k_m.gguf")
        self.assertEqual(self.find(), wanted)

    def test_gguf_split_model_loads_the_first_part(self):
        self.touch("hub/models--unsloth--Qwen3.5-4B-GGUF/snapshots/a/Q4_K_M/Qwen3.5-4B-Q4_K_M-00002-of-00002.gguf")
        wanted = self.touch("hub/models--unsloth--Qwen3.5-4B-GGUF/snapshots/b/Q4_K_M/Qwen3.5-4B-Q4_K_M-00001-of-00002.gguf")
        self.assertEqual(self.find(gguf_variant="Q4_K_M"), wanted)

    def test_gguf_explicit_file_wins_and_a_missing_one_falls_back(self):
        cached = self.touch(self.SNAP + "Qwen3.5-4B-Q4_K_M.gguf")
        explicit = self.touch("elsewhere/custom.gguf")
        self.assertEqual(self.find(gguf_file=str(explicit)), explicit)
        self.assertEqual(self.find(gguf_file=str(self.tmp / "gone.gguf")), cached)

    def test_gguf_extra_folders_are_searched_first_by_model_name(self):
        self.touch(self.SNAP + "Qwen3.5-4B-Q4_K_M.gguf")
        self.touch("models1/other/Llama-3-8B-Q4_K_M.gguf")
        wanted = self.touch("models2/unsloth/Qwen3.5-4B-GGUF/Qwen3.5-4B-Q4_K_M.gguf")
        os.environ["AGENT_ORCHESTRATOR_GGUF_DIRS"] = f"{self.tmp / 'models1'};{self.tmp / 'models2'};"
        self.assertEqual(self.find(gguf_variant="Q4_K_M"), wanted)

    def test_gguf_hugging_face_cache_from_hub_cache_or_hf_home(self):
        wanted = self.touch(self.SNAP + "Qwen3.5-4B-Q4_K_M.gguf")
        self.assertEqual(self.find(), wanted)
        del os.environ["HF_HUB_CACHE"]
        os.environ["HF_HOME"] = str(self.tmp)
        self.assertEqual(self.find(), wanted)

    def test_gguf_not_found_names_the_model_and_folders(self):
        os.environ["AGENT_ORCHESTRATOR_GGUF_DIRS"] = str(self.tmp / "models1")
        with self.assertRaises(orchestrator.WorkflowError) as ctx:
            self.find()
        text = str(ctx.exception)
        for part in (self.PATH, str(self.tmp / "models1"), "models--unsloth--Qwen3.5-4B-GGUF", "Unsloth", "Options"):
            self.assertIn(part, text)

    def test_server_args(self):
        self.assertEqual(orchestrator.llama_args(Path("m.gguf"), self.AUTO, 8899), [
            "-m", "m.gguf", "--host", "127.0.0.1", "--port", "8899", "--alias", self.PATH, "-c", "32768", "-ngl", "999", "--jinja",
            "--cache-type-k", "q8_0", "--cache-type-v", "q8_0", "-b", "1024", "-ub", "256", "--reasoning-budget", "0",
            "--flash-attn", "on"])
        for auto, layers in (({"gpu_layers": 20, "reasoning_budget": 8192}, "20"), ({}, "999")):
            args = orchestrator.llama_args(Path("m.gguf"), {"model_path": self.PATH, "max_seq_length": 4096, **auto}, 9000)
            self.assertEqual(args[-5:], ["-c", "4096", "-ngl", layers, "--jinja"])
            self.assertNotIn("--reasoning-budget", args)

    def test_llama_role_needs_no_key_and_uses_the_llama_port(self):
        client = self.client({"llama_port": 9001, "roles": {"planner": self.ROLE}})
        self.assertEqual((client.base_url, client.management_url, client.llama_port, client.remote),
                         ("http://127.0.0.1:9001/v1", "", 9001, False))
        self.assertEqual(client.load_mismatch(), "")
        self.assertEqual(client._monitor_row(0), {})

    def test_reuses_the_running_server_when_the_signature_matches(self):
        model_file = self.touch(self.SNAP + "Qwen3.5-4B-Q4_K_M.gguf")
        self.write_state(41, " ".join(orchestrator.llama_args(model_file, self.AUTO, 8899)))
        self.alive.add(41)
        self.assertEqual(self.client().ensure_model("planner", False), self.PATH)
        self.popen.assert_not_called()
        self.kill.assert_not_called()
        self.assertIn(self.PATH, self.loaded)
        self.emit.assert_any_call("model", role="planner", id=self.PATH)

    def test_restarts_the_server_when_the_signature_differs(self):
        model_file = self.touch(self.SNAP + "Qwen3.5-4B-Q4_K_M.gguf")
        self.write_state(41, "old args")
        self.alive.add(41)
        self.client().ensure_model("planner", False)
        self.kill.assert_called_once_with(41, orchestrator.signal.SIGTERM)
        args = orchestrator.llama_args(model_file, self.AUTO, 8899)
        call = self.popen.call_args
        self.assertEqual(call.args[0], [str(self.exe), *args])
        self.assertTrue(call.kwargs["creationflags"] & subprocess.DETACHED_PROCESS)
        log = self.tmp / "temp" / "agent-orchestrator-llama.log"
        self.assertEqual(json.loads(self.state_file().read_text()),
                         {"pid": 77, "port": 8899, "signature": " ".join(args), "model_file": str(model_file), "log": str(log)})
        self.emit.assert_any_call("loading", role="planner", path=self.PATH)  # no url: the launcher would unload via Unsloth
        self.assertIn(self.PATH, self.loaded)

    def test_restarts_an_unhealthy_server_with_a_matching_signature(self):
        model_file = self.touch(self.SNAP + "Qwen3.5-4B-Q4_K_M.gguf")
        self.write_state(41, " ".join(orchestrator.llama_args(model_file, self.AUTO, 8899)))
        self.alive.add(41)
        self.healthy.side_effect = [False, True]  # the reuse check, then the wait for the new server
        self.client().ensure_model("planner", False)
        self.kill.assert_called_once_with(41, orchestrator.signal.SIGTERM)
        self.popen.assert_called_once()

    def test_stop_waits_for_the_process_before_the_next_server_starts(self):
        model_file = self.touch(self.SNAP + "Qwen3.5-4B-Q4_K_M.gguf")
        self.write_state(41, "old args")
        events, states = [], iter([True, True, False])  # running, still running after the kill, gone

        def alive(pid):
            events.append("alive")
            return next(states, False)

        self.popen.side_effect = lambda *args, **kwargs: events.append("popen") or self.popen.return_value
        with patch.object(orchestrator, "llama_alive", side_effect=alive):
            self.client().ensure_model("planner", False)
        self.assertEqual(events, ["alive", "alive", "alive", "popen"])
        self.assertEqual(orchestrator.time.sleep.call_count, 1)  # one pause between the two polls after the kill

    def test_stop_polls_until_the_process_is_gone(self):
        self.write_state(41, "sig")
        with patch.object(orchestrator, "llama_alive", side_effect=[True, True, True, False]):
            self.assertTrue(orchestrator.llama_stop())
        self.assertEqual(orchestrator.time.sleep.call_count, 2)
        self.assertFalse(self.state_file().exists())

    def test_load_timeout_stops_the_server_and_names_the_log(self):
        self.touch(self.SNAP + "Qwen3.5-4B-Q4_K_M.gguf")
        self.healthy.return_value = False
        self.popen.side_effect = lambda *args, **kwargs: self.alive.add(77) or self.popen.return_value
        clock = iter(range(0, 1000, 20))  # 20 s per reading: past the 30 s timeout on the second
        with patch.object(orchestrator.time, "time", side_effect=lambda: next(clock)), \
                self.assertRaises(orchestrator.WorkflowError) as ctx:
            self.client().ensure_model("planner", False)
        self.assertIn("did not load", str(ctx.exception))
        self.assertIn("within 30s", str(ctx.exception))
        self.assertIn(str(self.tmp / "temp" / "agent-orchestrator-llama.log"), str(ctx.exception))
        self.kill.assert_called_once_with(77, orchestrator.signal.SIGTERM)
        self.assertFalse(self.state_file().exists())

    def chat(self, client, sampling, replies=None):
        """Run client.complete against canned payloads; returns the request bodies it sent."""
        replies = replies or [{"choices": [{"message": {"content": "ok"}, "finish_reason": "stop"}]}]
        with patch.object(client, "_request", side_effect=replies) as request, \
                patch.object(client, "context_size", return_value=None):  # complete() asks it; no live server here
            client.complete(self.PATH, [{"role": "user", "content": "hi"}], sampling)
        return [call.args[2] for call in request.call_args_list]

    CUT_OFF = {"choices": [{"message": {"content": ""}, "finish_reason": "length"}]}

    def test_llama_thinking_switch_goes_in_chat_template_kwargs(self):
        for sampling in ({"enable_thinking": False}, {"chat_template_kwargs": {"x": 1}, "enable_thinking": False}):
            (body,) = self.chat(self.client(), sampling)
            self.assertNotIn("enable_thinking", body)
            self.assertEqual(body["chat_template_kwargs"], {**sampling.get("chat_template_kwargs", {}), "enable_thinking": False})

    def test_llama_ask_once_without_thinking(self):
        sent = []
        with patch.object(orchestrator.ApiClient, "ensure_model", return_value=self.PATH), \
                patch.object(orchestrator.ApiClient, "context_size", return_value=None), \
                patch.object(orchestrator.ApiClient, "_request",
                             lambda self_, method, path, body: sent.append(body) or {"choices": [{"message": {"content": "ok"}, "finish_reason": "stop"}]}):
            self.assertEqual(orchestrator.ask_once({"roles": {"planner": self.ROLE}}, "planner", "s", "u", False, thinking=False), "ok")
        self.assertNotIn("enable_thinking", sent[0])
        self.assertEqual(sent[0]["chat_template_kwargs"], {"enable_thinking": False})

    def test_llama_cut_off_retry_disables_thinking_in_chat_template_kwargs(self):
        reply = {"choices": [{"message": {"content": "ok"}, "finish_reason": "stop"}]}
        first, retry = self.chat(self.client(), {"chat_template_kwargs": {"x": 1}}, [self.CUT_OFF, reply])
        self.assertEqual(first["chat_template_kwargs"], {"x": 1})
        self.assertNotIn("enable_thinking", retry)
        self.assertEqual(retry["chat_template_kwargs"], {"x": 1, "enable_thinking": False})

    def test_llama_reasoning_effort_goes_in_chat_template_kwargs(self):
        (body,) = self.chat(self.client(), {"chat_template_kwargs": {"x": 1}, "enable_thinking": True, "reasoning_effort": "high"})
        self.assertNotIn("reasoning_effort", body)
        self.assertEqual(body["chat_template_kwargs"], {"x": 1, "enable_thinking": True, "reasoning_effort": "high"})

    def test_unsloth_thinking_switch_stays_top_level(self):
        os.environ["AGENT_ORCHESTRATOR_BACKEND"] = "unsloth"
        os.environ["UNSLOTH_API_KEY"] = "k"
        reply = {"choices": [{"message": {"content": "ok"}, "finish_reason": "stop"}]}
        (body,) = self.chat(self.client(), {"enable_thinking": False})
        self.assertEqual((body["enable_thinking"], "chat_template_kwargs" in body), (False, False))
        _, retry = self.chat(self.client(), {}, [self.CUT_OFF, reply])
        self.assertEqual((retry["enable_thinking"], "chat_template_kwargs" in retry), (False, False))

    def test_gguf_warns_when_no_file_matches_the_variant(self):
        wanted = self.touch(self.SNAP + "Qwen3.5-4B-BF16.gguf")
        out = io.StringIO()
        with contextlib.redirect_stdout(out):
            self.assertEqual(self.find(gguf_variant="Q4_K_M"), wanted)
        self.assertIn("q4_k_m", out.getvalue())
        self.assertIn("Qwen3.5-4B-BF16.gguf", out.getvalue())
        out = io.StringIO()
        with contextlib.redirect_stdout(out):
            self.find()
        self.assertEqual(out.getvalue(), "")

    def test_early_exit_reports_the_log_tail(self):
        self.touch(self.SNAP + "Qwen3.5-4B-Q4_K_M.gguf")
        self.healthy.return_value = False

        def crash(cmd, **kwargs):
            kwargs["stdout"].write("".join(f"line {n}\n" for n in range(30)).encode())
            kwargs["stdout"].flush()
            return self.popen.return_value

        self.popen.side_effect = crash
        self.popen.return_value.poll.return_value = 3
        self.popen.return_value.returncode = 3
        with self.assertRaises(orchestrator.WorkflowError) as ctx:
            self.client().ensure_model("planner", False)
        text = str(ctx.exception)
        self.assertIn("code 3", text)
        self.assertIn("line 10", text)
        self.assertIn("line 29", text)
        self.assertNotIn("line 9", text)
        self.assertFalse(self.state_file().exists())

    def test_missing_llama_server_points_to_options(self):
        self.touch(self.SNAP + "Qwen3.5-4B-Q4_K_M.gguf")
        for value in (None, str(self.tmp / "nope.exe")):
            if value is None:
                del os.environ["AGENT_ORCHESTRATOR_LLAMA_SERVER"]
            else:
                os.environ["AGENT_ORCHESTRATOR_LLAMA_SERVER"] = value
            with self.assertRaises(orchestrator.WorkflowError) as ctx:
                self.client().ensure_model("planner", False)
            self.assertIn("Download llama-server in Options", str(ctx.exception))
        self.popen.assert_not_called()

    def test_backend_unset_keeps_the_unsloth_path(self):
        os.environ["UNSLOTH_API_KEY"] = "k"
        for backend in (None, "unsloth"):
            if backend is None:
                os.environ.pop("AGENT_ORCHESTRATOR_BACKEND", None)
            else:
                os.environ["AGENT_ORCHESTRATOR_BACKEND"] = backend
            client = self.client()
            self.assertEqual((client.base_url, client.management_url, client.llama_port),
                             ("http://127.0.0.1:8888/v1", "http://127.0.0.1:8888", 0))
            client.model_id = unittest.mock.Mock(side_effect=[orchestrator.WorkflowError("none"), self.PATH])
            with patch.object(orchestrator.ApiClient, "_request_at", return_value={}) as request:
                client.ensure_model("planner", False)
            request.assert_any_call("http://127.0.0.1:8888", "POST", "/api/inference/load", self.AUTO)
        self.popen.assert_not_called()

    def test_remote_roles_are_unaffected(self):
        os.environ["REMOTE_TEST_KEY"] = "sk"
        remote = {"base_url": "https://remote.example/v1", "api_key_env": "REMOTE_TEST_KEY", "model": "vendor/m"}
        client, _ = orchestrator.role_settings({"roles": {"planner": remote}}, "planner")
        self.assertEqual((client.base_url, client.remote, client.llama_port), ("https://remote.example/v1", True, 0))
        self.assertEqual(client.ensure_model("planner", False), "vendor/m")
        self.popen.assert_not_called()
        self.assertEqual(self.loaded, {})

    def test_unload_models_stops_the_recorded_server(self):
        self.write_state(41, "sig")
        self.alive.add(41)
        self.loaded[self.PATH] = self.client()
        with patch.object(orchestrator.ApiClient, "_request_at") as request:
            orchestrator.unload_models()
        self.kill.assert_called_once_with(41, orchestrator.signal.SIGTERM)
        request.assert_not_called()
        self.assertFalse(self.state_file().exists())
        self.assertEqual(self.loaded, {})

    def test_unload_local_flag_stops_the_recorded_server(self):
        self.write_state(41, "sig")
        self.alive.add(41)
        with patch.object(orchestrator.sys, "argv", ["orchestrator.py", "--unload-local"]), \
                contextlib.redirect_stdout(io.TextIOWrapper(io.BytesIO())):  # _run reconfigures stdout
            self.assertEqual(orchestrator._run(), 0)
        self.kill.assert_called_once_with(41, orchestrator.signal.SIGTERM)
        self.assertFalse(self.state_file().exists())

    def test_alive_only_for_a_llama_server_process(self):
        for image, expected in (('"llama-server.exe","41","Console"', True), ('"notepad.exe","41","Console"', False)):
            with patch.object(orchestrator.subprocess, "run", return_value=subprocess.CompletedProcess([], 0, image, "")):
                self.assertEqual(REAL_LLAMA_ALIVE(41), expected)


class ContextTests(unittest.TestCase):
    """The "context" event: after each model call of a --chat/--code turn, the prompt's split by category and n_ctx."""

    USAGE = {"prompt_tokens": 1237, "completion_tokens": 56}

    def setUp(self):
        tmp = tempfile.TemporaryDirectory()
        self.addCleanup(tmp.cleanup)
        self.base = Path(tmp.name).resolve()
        self.enterContext(patch.dict(orchestrator.CONTEXT_SIZES, clear=True))

    def turn(self, flag, request, replies, role=None):
        """Run one turn through the real ApiClient (stream/request/model checks patched); returns (events, bodies)."""
        role = role or {"base_url": "http://x/v1", "api_key": "k", "auto_load": {"model_path": "p", "max_seq_length": 4096}}
        config = {"roles": {r: role for r in orchestrator.BUILTIN_ROLES}}
        (self.base / "config.json").write_text(json.dumps(config), encoding="utf-8")
        req = self.base / "request.json"
        req.write_text(json.dumps(request), encoding="utf-8")
        events, bodies, replies = [], [], list(replies)

        def stream(client, body):
            bodies.append(body)
            reply = replies.pop(0)
            message = reply if isinstance(reply, dict) else {"role": "assistant", "content": reply}
            return {"choices": [{"message": message, "finish_reason": "stop"}], "usage": self.USAGE, "_streamed": True}

        title = {"choices": [{"message": {"role": "assistant", "content": "A Title"}, "finish_reason": "stop"}], "usage": self.USAGE}
        argv = ["o", "--project", str(self.base), flag, str(req), "--events"]
        out = io.StringIO()
        out.reconfigure = lambda **kw: None
        with patch.object(orchestrator.ApiClient, "_stream_chat", stream), \
                patch.object(orchestrator.ApiClient, "_request", lambda client, method, route, body=None: title), \
                patch.object(orchestrator.ApiClient, "ensure_model", lambda client, role, interactive: "m"), \
                patch.object(orchestrator.ApiClient, "_monitor_row", lambda client, started: {}), \
                patch.object(orchestrator, "emit", lambda e, **d: events.append((e, d))), \
                patch.object(orchestrator, "unload_models", lambda: None), patch.object(orchestrator.sys, "argv", argv), \
                patch.object(orchestrator.sys, "stdout", out):
            self.assertEqual(orchestrator.main(), 0)
        return [d for e, d in events if e == "context"], bodies

    MSGS = [{"role": "user", "content": "hi"}, {"role": "assistant", "content": "hello"}, {"role": "user", "content": "explain X"}]

    def test_chat_turn_emits_context_and_the_title_call_does_not(self):
        contexts, bodies = self.turn("--chat", {"mode": "single", "worker": "implementer", "messages": self.MSGS, "title": True},
                                     ["the answer"])
        self.assertEqual(len(contexts), 1)  # the quiet title call emitted nothing
        ctx = contexts[0]
        self.assertEqual((ctx["role"], ctx["n_ctx"], ctx["prompt_tokens"], ctx["completion_tokens"]), ("implementer", 4096, 1237, 56))
        self.assertEqual(set(ctx["parts"]), {"system", "tools", "skills", "messages", "tool_results"})
        self.assertEqual(sum(ctx["parts"].values()), 1237)
        self.assertEqual((ctx["parts"]["skills"], ctx["parts"]["tool_results"]), (0, 0))
        self.assertTrue(ctx["parts"]["system"] > 0 and ctx["parts"]["messages"] > 0 and ctx["parts"]["tools"] > 0)  # delegate is always offered

    def test_tools_and_skills_parts_when_enabled(self):
        skill = self.base / "skill"
        skill.mkdir()
        (skill / "SKILL.md").write_text("---\nname: demo\ndescription: does demo things\n---\nbody", encoding="utf-8")
        contexts, _ = self.turn("--chat", {"mode": "single", "worker": "implementer", "messages": self.MSGS, "search": True,
                                           "skills": [str(skill)]}, ["the answer"])
        parts = contexts[0]["parts"]
        self.assertTrue(parts["tools"] > 0 and parts["skills"] > 0, parts)
        self.assertEqual(sum(parts.values()), 1237)

    def test_delegated_sub_agent_calls_emit_too(self):
        contexts, _ = self.turn("--chat", {"messages": self.MSGS}, [tool_call("delegate", worker="implementer", brief="b"), "REPORT", "FINAL"])
        self.assertEqual([c["role"] for c in contexts], ["planner", "implementer", "planner"])
        self.assertTrue(all(sum(c["parts"].values()) == 1237 for c in contexts))

    def test_code_turn_counts_tool_results(self):
        root = self.base / "app"
        root.mkdir()
        (root / "a.txt").write_text("x" * 500, encoding="utf-8")
        contexts, _ = self.turn("--code", {"action": "chat", "worker": "planner", "messages": [{"role": "user", "content": "read a"}],
                                           "folders": [{"path": str(root), "access": "write"}], "title": True},
                                [tool_call("read_file", path="a.txt"), "done"])
        self.assertEqual(len(contexts), 2)
        self.assertEqual(contexts[0]["parts"]["tool_results"], 0)
        self.assertGreater(contexts[1]["parts"]["tool_results"], 0)
        self.assertGreater(contexts[1]["parts"]["tools"], 0)  # the Code file tools
        self.assertTrue(all(sum(c["parts"].values()) == 1237 for c in contexts))

    def test_n_ctx_sources(self):
        llama = orchestrator.ApiClient({"base_url": "http://127.0.0.1:8899/v1", "api_key": "no-key", "auto_load": {"model_path": "q"}})
        llama.llama_port = 8899
        asked = []

        def props(base, method, route, body=None):
            asked.append(f"{base}{route}")
            return {"default_generation_settings": {"n_ctx": 32768}}

        with patch.object(llama, "_request_at", props):
            self.assertEqual((llama.context_size(), llama.context_size()), (32768, 32768))
        self.assertEqual(asked, ["http://127.0.0.1:8899/props"])  # cached per process
        unsloth = orchestrator.ApiClient({"base_url": "http://u/v1", "management_url": "http://u", "api_key": "k",
                                          "auto_load": {"model_path": "p", "max_seq_length": 8192}})
        with patch.object(unsloth, "_request_at", side_effect=orchestrator.WorkflowError("down")):
            self.assertEqual(unsloth.context_size(), 8192)  # status unavailable: the role's max_seq_length
        orchestrator.CONTEXT_SIZES.clear()
        with patch.object(unsloth, "_request_at", return_value={"context_length": 16384}):
            self.assertEqual(unsloth.context_size(), 16384)
        remote = orchestrator.ApiClient({"base_url": "https://api.example/v1", "api_key": "k", "model": "gpt"})
        self.assertIsNone(remote.context_size())

    def test_parts_sum_exactly_with_rounding(self):
        for prompt in (0, 1, 7, 999, 100003):
            parts = orchestrator.context_parts(
                [{"role": "system", "content": "sys\n\nSKILLS\n- a: b\n\n=== YOUR ROLE"}, {"role": "user", "content": "q" * 13},
                 {"role": "tool", "content": "r" * 29}], [{"type": "function"}], prompt)
            self.assertEqual(sum(parts.values()), prompt)


if __name__ == "__main__":
    unittest.main()
