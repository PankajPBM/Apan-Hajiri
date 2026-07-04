"use client";

import { useEffect } from "react";
import { useRouter } from "next/navigation";

export default function AccessControlHolidayPage() {
  const router = useRouter();

  useEffect(() => {
    router.replace("/access-control?section=time-period");
  }, [router]);

  return null;
}
