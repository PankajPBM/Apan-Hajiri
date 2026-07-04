"use client";

import { useEffect } from "react";
import { useRouter } from "next/navigation";

export default function AccessControlTimePeriodPage() {
  const router = useRouter();

  useEffect(() => {
    router.replace("/access-control?section=time-period");
  }, [router]);

  return null;
}
