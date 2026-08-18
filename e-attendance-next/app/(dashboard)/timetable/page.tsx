"use client";

import { useEffect } from "react";
import { useRouter } from "next/navigation";

export default function TimetablePage() {
  const router = useRouter();

  useEffect(() => {
    router.replace("/shifts?tab=timetable");
  }, [router]);

  return null;
}
