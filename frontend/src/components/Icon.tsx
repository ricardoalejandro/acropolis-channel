const paths: Record<string, string> = {
  eye: 'M2 12s3.5-7 10-7 10 7 10 7-3.5 7-10 7S2 12 2 12ZM15 12a3 3 0 1 1-6 0 3 3 0 0 1 6 0Z',
  'eye-off':
    'm3 3 18 18M10 5.2A13 13 0 0 1 12 5c6.5 0 10 7 10 7a19 19 0 0 1-3.3 4.2M6.2 6.2A20 20 0 0 0 2 12s3.5 7 10 7a13 13 0 0 0 5.8-1.4M9.9 9.9a3 3 0 0 0 4.2 4.2',
  search: 'm21 21-5-5M10.5 3a7.5 7.5 0 1 0 0 15 7.5 7.5 0 0 0 0-15',
  book: 'M12 5v15M3 4c4-1 6 0 9 2 3-2 5-3 9-2v15c-4-1-6 0-9 2-3-2-5-3-9-2Z',
  video: 'M4 4h16v16H4zM9 8l7 4-7 4z',
  film: 'M3 3h18v18H3zM7 3v18M17 3v18M3 8h4M3 16h4M17 8h4M17 16h4',
  audio: 'M4 13v-2a8 8 0 0 1 16 0v2M4 11H2v8h4v-8ZM20 11h2v8h-4v-8Z',
  talk: 'M3 3h18v14H8l-5 4Z M7 7h10M7 11h7',
  course: 'M3 4h18v13H3zM12 17v4M7 21h10M7 8h10M7 12h7',
  user: 'M12 3a4 4 0 1 0 0 8 4 4 0 0 0 0-8M4 21v-2a8 8 0 0 1 16 0v2',
  shield: 'M12 2 3 6v6c0 5 9 10 9 10s9-5 9-10V6ZM8 12l3 3 5-6',
  grid: 'M3 3h7v7H3zM14 3h7v7h-7zM3 14h7v7H3zM14 14h7v7h-7z',
  menu: 'M3 6h18M3 12h18M3 18h18',
  play: 'm7 3 14 9-14 9Z',
};
export function Icon({ name }: { name: string }) {
  return (
    <svg
      className="icon"
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeWidth="1.6"
      strokeLinecap="round"
      strokeLinejoin="round"
      aria-hidden="true"
    >
      <path d={paths[name] ?? paths['grid']} />
    </svg>
  );
}
