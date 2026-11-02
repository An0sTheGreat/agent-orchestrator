---
name: start-project
description: "Use when the user calls start-project, /start-project, or $start-project to begin the autonomous project run on this folder's PROJECT.md."
---

# Start Project

Begin the autonomous project run, the same run the launcher's Start project button starts. The run works through PROJECT.md on its own after this reply ends; you do not do the project's work in this turn.

1. Check that PROJECT.md exists in the project root with read_file. If it is missing, say so and suggest init-projMD; do not create it here and do not start.
2. Check that it is filled in: a real Project Goal and Completion Criteria, not the template's placeholders. If it is not, say what is missing and do not start.
3. Otherwise call start_project once. Then tell the user, in one or two sentences, that the run starts when this reply ends and what it will work toward (the goal from PROJECT.md). Stop there.

start_project is only available on a turn where the user asked to start the project. Never call it for any other reason.
