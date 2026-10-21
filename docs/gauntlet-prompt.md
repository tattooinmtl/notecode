# The gauntlet prompt

Paste-ready, for the notecode app at `C:\NexusOne\prototypes\notecode`.

The bar, the measurable half and the scope were chosen before this was written:
**Microsoft's official WinForms and .NET desktop guidance, accessibility rules
included**; the **a11y and UX gaps the current test suite misses**; and **fix and
audit** — the loop may change the app, and then re-audit until the result holds.

Two passes have already been run this way. `finalAudit.md` is what they produced,
and it ends with the items that are still open. This prompt starts from there, so
the second run does not re-report what the first one already fixed.

```
Audit the notecode app at C:\NexusOne\prototypes\notecode end to end, into finalAudit.md.

The bar is Microsoft's official WinForms and .NET desktop guidance, accessibility rules included. Fetch the real thing and compare against it directly, not against a description of it. The measurable half is the a11y and UX gaps the current suite misses: every gap must be one that no script in scripts/ catches today, and every fix must land with the check that now catches it.

Read scripts/ first. The guards that are already there tell you what the suite already covers, and what it does not is the point.

finalAudit.md already holds two passes. Read the open items at the end of it and start there: the tab strip's contents are invisible to a screen reader, the app cannot follow a high-contrast theme, the custom painting does not scale with the display, there are no access keys outside the menus, and the tree items do not say what they are. Do not re-report anything the file already records as fixed.

Break this into the smallest pieces that can be judged on their own - the tab strip, the agent panel, the composer, the transcript, the tree, the dialogs. For each piece, fan out a builder and a separate critic with fresh context. The critic drives the real running app, puts what it finds next to Microsoft's guidance side by side, says which is better, and names the single biggest remaining gap. Then it goes back to the builder.

The critic should be a harsh critic. Praise is not useful. If ours does not win, it keeps going.

Keep looping until the critic picks ours. Run the builders and critics as parallel subagents.

Keep finalAudit.md updating as the work evolves so I can watch it.
```

**I can run this here.**
