# App Shell Overrides

These rules override `MASTER.md` for every TuneForge screen.

- TuneForge is a desktop app, so ignore the master's hero, landing page, CTA and scroll-reveal guidance.
- Fonts ship inside the app. Never load Google Fonts at runtime.
- Use the colour tokens in `src/ui/src/index.css`, including their dark values. Do not use raw hex in components.
- Use the primary colour for main actions. Keep the orange accent out unless a screen needs it.
- Motion stays at 200ms or less, and it respects reduced motion.
- Do not show placeholder or made-up data.
