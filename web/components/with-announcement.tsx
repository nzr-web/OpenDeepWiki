"use client";

import type { ReactNode } from "react";
// NzrAiWiki: sponsor banner disabled
// import { AnnouncementBanner } from "@/components/announcement-banner";
import { cn } from "@/lib/utils";

interface WithAnnouncementProps {
  children: ReactNode;
  className?: string;
}

export function WithAnnouncement({ children, className }: WithAnnouncementProps) {
  return (
    <div className={cn("flex min-h-svh w-full flex-col", className)}>
      {/* NzrAiWiki: sponsor banner disabled */}
      {/* <AnnouncementBanner /> */}
      <div className="flex min-h-0 min-w-0 flex-1 flex-col">{children}</div>
    </div>
  );
}
