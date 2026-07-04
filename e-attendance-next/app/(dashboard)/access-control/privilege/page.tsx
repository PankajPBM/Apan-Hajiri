"use client";

import { useEffect } from "react";
import { useRouter } from "next/navigation";

export default function AccessControlPrivilegePage() {
  const router = useRouter();

  useEffect(() => {
    router.replace("/access-control?section=privilege");
  }, [router]);

  return null;
}
