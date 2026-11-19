/** The dialog's styles, adopted as a constructable stylesheet (the shell's policy blocks <style> elements, §7.14). */
export const scanStyles = `
.nk-ns { display: flex; flex-direction: column; gap: 0.75rem; font-size: 0.875rem; }
.nk-ns-title { margin: 0; font-size: 0.9375rem; font-weight: 650; }
.nk-ns-muted { color: var(--color-text-muted, #9aa8c1); }
.nk-ns-mono { font-family: ui-monospace, 'Cascadia Mono', Consolas, monospace; font-size: 0.8125rem; }
.nk-ns-bar { display: flex; flex-wrap: wrap; align-items: center; gap: 0.5rem; }
.nk-ns-end { justify-content: flex-end; }
.nk-ns-field { display: flex; flex-direction: column; gap: 0.25rem; font-weight: 600; }
.nk-ns-field .nk-ns-muted { font-weight: 400; }
.nk-ns-check { display: flex; gap: 0.5rem; align-items: flex-start; }
.nk-ns-input { min-height: 2rem; padding: 0 0.5rem; border: 1px solid var(--color-border-strong, #33456b); border-radius: var(--radius-md, 8px);
  background: var(--color-input-bg, #0b1220); color: inherit; font: inherit; font-weight: 400; }
.nk-ns-warning { margin: 0; padding: 0.625rem 0.75rem; border: 1px solid var(--color-warning, #f5b84b); border-radius: var(--radius-md, 8px); }
.nk-ns-error { margin: 0; padding: 0.5rem 0.75rem; border: 1px solid var(--color-danger-border, #b91c1c); border-radius: var(--radius-md, 8px); color: var(--color-danger-text, #fca5a5); }
.nk-ns-notice { margin: 0; padding: 0.5rem 0.75rem; border: 1px solid var(--color-border-strong, #33456b); border-radius: var(--radius-md, 8px); }
.nk-ns-button { display: inline-flex; align-items: center; min-height: 2rem; padding: 0 0.75rem; border: 1px solid var(--color-border-strong, #33456b);
  border-radius: var(--radius-md, 8px); background: transparent; color: inherit; font: inherit; font-size: 0.8125rem; font-weight: 600; cursor: pointer; }
.nk-ns-button:hover:not(:disabled) { background: var(--color-surface-raised, #16213a); }
.nk-ns-button:disabled { opacity: 0.5; cursor: default; }
.nk-ns-button[data-primary] { background: var(--color-accent, #2563eb); border-color: var(--color-accent, #2563eb); color: var(--color-on-accent, #fff); }
.nk-ns-button[data-danger] { border-color: var(--color-danger-border, #b91c1c); color: var(--color-danger-text, #fca5a5); }
.nk-ns-chip { padding: 0.125rem 0.5rem; border: 1px solid var(--color-border-strong, #33456b); border-radius: 999px; background: transparent; color: inherit;
  font-family: ui-monospace, 'Cascadia Mono', Consolas, monospace; font-size: 0.8125rem; cursor: pointer; }
.nk-ns-link { padding: 0; border: 0; background: none; color: var(--color-accent-text, #8fb4ff); font: inherit; cursor: pointer; }
.nk-ns-scroll { overflow: auto; max-height: 55vh; border: 1px solid var(--color-border, #233150); border-radius: var(--radius-md, 8px); }
.nk-ns-table { width: 100%; border-collapse: collapse; }
.nk-ns-table th, .nk-ns-table td { padding: 0.375rem 0.5rem; border-bottom: 1px solid var(--color-border, #233150); text-align: left; vertical-align: top; }
.nk-ns-table thead th { position: sticky; top: 0; background: var(--color-surface, #111a2e); color: var(--color-text-muted, #9aa8c1); font-weight: 600; }
.nk-ns-port { display: inline-block; margin: 0 0.25rem 0.25rem 0; padding: 0 0.375rem; border-radius: 999px; background: var(--color-surface-raised, #16213a); font-size: 0.75rem; }
.nk-ns-port[data-known] { background: var(--color-accent, #2563eb); color: var(--color-on-accent, #fff); }
.nk-ns-details { display: grid; grid-template-columns: max-content 1fr; gap: 0.25rem 1rem; margin: 0; }
.nk-ns-details dt { color: var(--color-text-muted, #9aa8c1); }
.nk-ns-details dd { margin: 0; }
.nk-ns-details ul { margin: 0; padding-left: 1rem; }
`
