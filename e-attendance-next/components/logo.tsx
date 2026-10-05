import React from "react";

export function ApanHajiriLogo({ className = "h-8 w-8", size = 32 }: { className?: string; size?: number }) {
  return (
    <svg
      width={size}
      height={size}
      viewBox="0 0 120 120"
      fill="none"
      xmlns="http://www.w3.org/2000/svg"
      className={className}
    >
      <defs>
        {/* Modern Base Gradient */}
        <linearGradient id="apanGrad" x1="0%" y1="0%" x2="100%" y2="100%">
          <stop offset="0%" stopColor="#2563EB" />
          <stop offset="50%" stopColor="#1D4ED8" />
          <stop offset="100%" stopColor="#0F172A" />
        </linearGradient>

        {/* Checkmark Accent Gradient */}
        <linearGradient id="checkGrad" x1="0%" y1="0%" x2="100%" y2="100%">
          <stop offset="0%" stopColor="#34D399" />
          <stop offset="100%" stopColor="#059669" />
        </linearGradient>

        {/* Subtle Inner Glow */}
        <filter id="softGlow" x="-20%" y="-20%" width="140%" height="140%">
          <feDropShadow dx="0" dy="4" stdDeviation="6" floodColor="#2563EB" floodOpacity="0.25" />
        </filter>
      </defs>

      {/* Smooth Squircle Container */}
      <rect
        x="6"
        y="6"
        width="108"
        height="108"
        rx="28"
        fill="url(#apanGrad)"
        filter="url(#softGlow)"
      />
      <rect
        x="6"
        y="6"
        width="108"
        height="108"
        rx="28"
        stroke="rgba(255, 255, 255, 0.16)"
        strokeWidth="1.5"
      />

      {/* Biometric Fingerprint Geometric Nodes */}
      <path
        d="M38 68 C 38 46, 82 46, 82 68"
        stroke="rgba(255, 255, 255, 0.3)"
        strokeWidth="4.5"
        strokeLinecap="round"
      />
      <path
        d="M47 70 C 47 54, 73 54, 73 70"
        stroke="rgba(255, 255, 255, 0.45)"
        strokeWidth="4.5"
        strokeLinecap="round"
      />
      <path
        d="M56 72 C 56 62, 64 62, 64 72"
        stroke="rgba(255, 255, 255, 0.6)"
        strokeWidth="4"
        strokeLinecap="round"
      />

      {/* Modern High-Precision Checkmark Verification */}
      <path
        d="M40 60 L54 74 L84 40"
        stroke="url(#checkGrad)"
        strokeWidth="8.5"
        strokeLinecap="round"
        strokeLinejoin="round"
      />

      {/* Center Precision Indicator Dot */}
      <circle cx="84" cy="40" r="3.5" fill="#6EE7B7" />
    </svg>
  );
}
