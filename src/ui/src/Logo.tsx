/** The app icon, with the same dark tile on every theme. */
export function Logo({ className = "size-6" }: { className?: string }) {
  return (
    <svg aria-hidden="true" viewBox="0 0 256 256" className={`${className} shrink-0`}>
      <rect width="256" height="256" rx="48" fill="#0d0d0d" />
      <g fill="none" strokeWidth="34" strokeLinecap="round">
        <path d="M 62.9 199.1 A 92 92 0 1 1 193.1 199.1" stroke="#666666" />
        <path d="M 62.9 199.1 A 92 92 0 0 1 185 62" stroke="#ffffff" />
      </g>
      <polygon points="184,78 104,128 136,160" fill="#ffffff" />
      <polygon points="184,78 136,160 120,144" fill="#a8a8a8" />
    </svg>
  );
}
