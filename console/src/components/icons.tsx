/**
 * Small hand-drawn outline icons for the project nav — same spirit as ui.tsx's Logo (inline SVG,
 * no icon-library dependency). Fixed 16px, stroke="currentColor" so they inherit the nav item's
 * text color automatically (active/hover states need no icon-specific styling).
 */
import type { SVGProps } from "react";

type IconProps = Omit<SVGProps<SVGSVGElement>, "viewBox" | "fill" | "xmlns">;

function Icon({ children, ...props }: IconProps & { children: React.ReactNode }) {
  return (
    <svg
      viewBox="0 0 16 16"
      fill="none"
      xmlns="http://www.w3.org/2000/svg"
      strokeWidth="1.5"
      strokeLinecap="round"
      strokeLinejoin="round"
      {...props}
    >
      {children}
    </svg>
  );
}

export function OverviewIcon(props: IconProps) {
  return (
    <Icon {...props}>
      <rect x="2" y="2" width="5" height="5" rx="1" stroke="currentColor" />
      <rect x="9" y="2" width="5" height="5" rx="1" stroke="currentColor" />
      <rect x="2" y="9" width="5" height="5" rx="1" stroke="currentColor" />
      <rect x="9" y="9" width="5" height="5" rx="1" stroke="currentColor" />
    </Icon>
  );
}

export function UsersIcon(props: IconProps) {
  return (
    <Icon {...props}>
      <circle cx="8" cy="5.5" r="2.5" stroke="currentColor" />
      <path d="M2.5 14c0-2.5 2.4-4 5.5-4s5.5 1.5 5.5 4" stroke="currentColor" />
    </Icon>
  );
}

export function DatabasesIcon(props: IconProps) {
  return (
    <Icon {...props}>
      <ellipse cx="8" cy="3.5" rx="5" ry="1.75" stroke="currentColor" />
      <path d="M3 3.5V12c0 1 2.2 1.75 5 1.75s5-.75 5-1.75V3.5" stroke="currentColor" />
      <path d="M3 8c0 1 2.2 1.75 5 1.75s5-.75 5-1.75" stroke="currentColor" />
    </Icon>
  );
}

export function RealtimeIcon(props: IconProps) {
  return (
    <Icon {...props}>
      <path d="M8.5 1.5 3 9h4l-.5 5.5L13 7H9l-.5-5.5Z" stroke="currentColor" fill="currentColor" fillOpacity="0.12" />
    </Icon>
  );
}

export function WebhooksIcon(props: IconProps) {
  return (
    <Icon {...props}>
      <path d="M6.3 9.7 9.7 6.3" stroke="currentColor" />
      <path d="M7.7 4.3 9 3a2.5 2.5 0 1 1 3.5 3.5l-1.3 1.3" stroke="currentColor" />
      <path d="M8.3 11.7 7 13A2.5 2.5 0 1 1 3.5 9.5l1.3-1.3" stroke="currentColor" />
    </Icon>
  );
}

export function FunctionsIcon(props: IconProps) {
  return (
    <Icon {...props}>
      <path d="M6 2c-1.5 0-2 .8-2 2v2.3c0 .7-.3 1.2-1.5 1.7 1.2.5 1.5 1 1.5 1.7V12c0 1.2.5 2 2 2" stroke="currentColor" />
      <path d="M10 2c1.5 0 2 .8 2 2v2.3c0 .7.3 1.2 1.5 1.7-1.2.5-1.5 1-1.5 1.7V12c0 1.2-.5 2-2 2" stroke="currentColor" />
    </Icon>
  );
}

export function MessagingIcon(props: IconProps) {
  return (
    <Icon {...props}>
      <rect x="1.75" y="3.5" width="12.5" height="9" rx="1.5" stroke="currentColor" />
      <path d="M2.3 4.2l5.2 4.3a1 1 0 0 0 1 0l5.2-4.3" stroke="currentColor" />
    </Icon>
  );
}

export function ApiKeysIcon(props: IconProps) {
  return (
    <Icon {...props}>
      <circle cx="5" cy="8" r="3" stroke="currentColor" />
      <path d="M7.8 8h6.4M11 8v2.2M13 8v1.6" stroke="currentColor" />
    </Icon>
  );
}

export function PlatformsIcon(props: IconProps) {
  return (
    <Icon {...props}>
      <rect x="1.75" y="2.5" width="12.5" height="8" rx="1.25" stroke="currentColor" />
      <path d="M5.5 13.5h5M8 10.5v3" stroke="currentColor" />
    </Icon>
  );
}

export function AuditIcon(props: IconProps) {
  return (
    <Icon {...props}>
      <path d="M4 1.75h6.5L13 4.25V13a1.25 1.25 0 0 1-1.25 1.25h-6.5A1.25 1.25 0 0 1 4 13z" stroke="currentColor" />
      <path d="M5.75 7h4.5M5.75 9.5h4.5M5.75 12h2.5" stroke="currentColor" />
    </Icon>
  );
}

export function MenuIcon(props: IconProps) {
  return (
    <Icon {...props}>
      <path d="M2.5 4.5h11M2.5 8h11M2.5 11.5h11" stroke="currentColor" />
    </Icon>
  );
}

export function TablesIcon(props: IconProps) {
  return (
    <Icon {...props}>
      <rect x="1.75" y="2.5" width="12.5" height="11" rx="1.25" stroke="currentColor" />
      <path d="M1.75 6.5h12.5M6 6.5V13.5" stroke="currentColor" />
    </Icon>
  );
}

export function SitesIcon(props: IconProps) {
  return (
    <Icon {...props}>
      <rect x="1.75" y="2.75" width="12.5" height="10.5" rx="1.25" stroke="currentColor" />
      <path d="M1.75 5.5h12.5" stroke="currentColor" />
      <circle cx="3.75" cy="4.1" r="0.4" fill="currentColor" stroke="none" />
      <circle cx="5.25" cy="4.1" r="0.4" fill="currentColor" stroke="none" />
    </Icon>
  );
}
