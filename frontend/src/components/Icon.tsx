import type { SVGProps } from 'react';

const paths = {
  home: 'm3 10 9-7 9 7v10a1 1 0 0 1-1 1h-5v-7H9v7H4a1 1 0 0 1-1-1Z',
  library: 'M4 4h5v16H4zM13 4h5v16h-5zM3 8h7M12 8h7',
  settings:
    'M9 3h6l1 3 3 1 2 5-2 5-3 1-1 3H9l-1-3-3-1-2-5 2-5 3-1Z M15 12a3 3 0 1 1-6 0 3 3 0 0 1 6 0',
  history: 'M3 11a9 9 0 1 1 2.6 7.4M3 4v7h7M12 7v5l3 2',
  search: 'M16 10a6 6 0 1 1-12 0 6 6 0 0 1 12 0m-1.5 4.5L21 21',
  arrow: 'M4 12h16m-6-6 6 6-6 6',
  back: 'M20 12H4m6-6-6 6 6 6',
  play: 'm8 4 12 8-12 8Z',
  pause: 'M7 4v16M17 4v16',
  rewind: 'M3 5v6h6M3 11a9 9 0 1 1 2 7M12 7v5l-3 2',
  forward: 'M21 5v6h-6m6 0a9 9 0 1 0-2 7M12 7v5l3 2',
  volume: 'm11 4-6 5H2v6h3l6 5ZM15 8a6 6 0 0 1 0 8m3-11a10 10 0 0 1 0 14',
  muted: 'm11 4-6 5H2v6h3l6 5ZM16 9l6 6m0-6-6 6',
  fullscreen: 'M8 3H3v5m13-5h5v5M3 16v5h5m13-5v5h-5',
  plus: 'M12 4v16M4 12h16',
  scan: 'M8 3H3v5m13-5h5v5M3 16v5h5m13-5v5h-5M7 12h10',
  trash: 'M3 6h18M9 6V3h6v3M5 6l1 15h12l1-15M10 10v7m4-7v7',
  logout: 'M9 4H4v16h5m6-13 5 5-5 5m-6-5h11',
  check: 'm5 12 4 4L19 6',
  users:
    'M16 21v-3a4 4 0 0 0-4-4H6a4 4 0 0 0-4 4v3M13 6a4 4 0 1 1-8 0 4 4 0 0 1 8 0m4-3a4 4 0 0 1 0 8m3 10v-3a4 4 0 0 0-3-4',
  activity: 'M2 12h5l3-8 4 16 3-8h5',
  shield: 'm12 2 9 4v6c0 5-9 10-9 10S3 17 3 12V6ZM8 12l3 3 5-6',
  refresh:
    'M3 10a9 9 0 0 1 15-6l3 3M21 2v5h-5M21 14a9 9 0 0 1-15 6l-3-3M3 22v-5h5',
} as const;
export type IconName = keyof typeof paths;
export default function Icon({
  name,
  ...props
}: SVGProps<SVGSVGElement> & { name: IconName }) {
  return (
    <svg
      width="20"
      height="20"
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      strokeWidth="1.7"
      strokeLinecap="round"
      strokeLinejoin="round"
      aria-hidden="true"
      focusable="false"
      {...props}
    >
      <path d={paths[name]} />
    </svg>
  );
}
