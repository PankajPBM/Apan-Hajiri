import { Suspense } from "react";
import { DashboardLayoutClient } from "./layout-client";

export default function DashboardLayout({ children }: Readonly<{ children: React.ReactNode }>) {
  return (
    <Suspense
      fallback={
        <div className="grid min-h-screen place-items-center bg-[#f2f6fb]">
          <div className="rounded-lg border border-[#d9e8f7] bg-white px-4 py-2 text-sm text-[#1d446b]">
            Loading...
          </div>
        </div>
      }
    >
      <DashboardLayoutClient>{children}</DashboardLayoutClient>
    </Suspense>
  );
}
