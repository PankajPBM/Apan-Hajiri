"use client";

import { useEffect } from "react";
import { useRouter } from "next/navigation";

export default function AccessControlGroupsPage() {
  const router = useRouter();

  useEffect(() => {
    router.replace("/access-control?section=groups");
  }, [router]);

  return null;
}
