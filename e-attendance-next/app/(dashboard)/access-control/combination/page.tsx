"use client";

import { useEffect } from "react";
import { useRouter } from "next/navigation";

export default function AccessControlCombinationPage() {
  const router = useRouter();

  useEffect(() => {
    router.replace("/access-control?section=combination");
  }, [router]);

  return null;
}
