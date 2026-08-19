"use client";

import { useEffect } from "react";
import { useRouter } from "next/navigation";

export default function LegacyAttendancePage() {
  const router = useRouter();

  useEffect(() => {
    router.replace("/transactions");
  }, [router]);

  return (
    <div className="p-8 text-center text-sm font-medium text-slate-500">
      Redirecting to Transactions...
    </div>
  );
}
