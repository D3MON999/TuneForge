# App Shell Overrides

These rules override `MASTER.md` for every TuneForge screen.

- TuneForge is a desktop app, so ignore the master's hero landing page, CTA and scroll-reveal guidance.
- Use Segoe UI Variable, which comes with Windows, with Segoe UI as the fallback. Never load web fonts at runtime.
- Obsidian is the default theme: black surfaces, thin dark lines, white as the accent, and colour only for meaning.
- The other themes are Fluent, Hero, Forge and Light. Each one is a block of tokens in `src/ui/src/index.css`.
- Do not use raw hex in components, except the theme swatches in `theme.ts`.
- Every page opens with the `.hero` banner. On/off choices are switches, with the label on the left.
- Keep status colours for status: good, warning and bad, each with an icon or a word.
- Motion stays at 200ms or less, and it respects reduced motion.
- Do not show placeholder or made-up data.
- Look at a real screenshot in every theme you touch before calling a change finished.
