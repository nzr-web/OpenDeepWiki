"use client";

import { useAuth } from "@/contexts/auth-context";

// NzrAiWiki: запускать генерацию вики может только админ (бэкенд проверяет политику AdminOnly).
export function useIsAdmin(): boolean {
  const { user } = useAuth();
  return user?.roles?.includes("Admin") ?? false;
}
