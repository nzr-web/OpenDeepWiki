"use client";

import React, { useEffect, useState } from "react";
import Link from "next/link";
import { BRAND_NAME } from "@/lib/brand";
import { usePathname } from "next/navigation";
import {
  BookOpen,
  Bot,
  Building2,
  ChevronRight,
  Cog,
  Github,
  GitBranch,
  Home,
  KeyRound,
  LayoutDashboard,
  MessageCircle,
  Shield,
  Users,
  Wrench,
} from "lucide-react";

import { Badge } from "@/components/ui/badge";
import { api } from "@/lib/api-client";
import { useTranslations } from "@/hooks/use-translations";
import {
  Sidebar,
  SidebarContent,
  SidebarFooter,
  SidebarGroup,
  SidebarGroupContent,
  SidebarGroupLabel,
  SidebarHeader,
  SidebarMenu,
  SidebarMenuButton,
  SidebarMenuItem,
  SidebarMenuSub,
  SidebarMenuSubButton,
  SidebarMenuSubItem,
  SidebarRail,
} from "@/components/animate-ui/components/radix/sidebar";

interface NavLeaf {
  href: string;
  label: string;
}

interface NavItem {
  href?: string;
  icon: React.ComponentType<{ className?: string }>;
  label: string;
  children?: NavLeaf[];
}

interface NavGroup {
  label: string;
  items: NavItem[];
}

const getNavGroups = (t: (key: string) => string): NavGroup[] => [
  {
    label: t("admin.nav.overview"),
    items: [
      {
        href: "/admin",
        icon: LayoutDashboard,
        label: t("common.admin.dashboard"),
      },
    ],
  },
  {
    label: t("admin.nav.content"),
    items: [
      {
        href: "/admin/repositories",
        icon: GitBranch,
        label: t("common.admin.repositories"),
      },
      {
        href: "/admin/github-import",
        icon: Github,
        label: t("admin.githubImport.title"),
      },
    ],
  },
  {
    label: t("admin.nav.organization"),
    items: [
      {
        href: "/admin/users",
        icon: Users,
        label: t("common.admin.users"),
      },
      {
        href: "/admin/roles",
        icon: Shield,
        label: t("common.admin.roles"),
      },
      {
        href: "/admin/departments",
        icon: Building2,
        label: t("admin.departments.title"),
      },
    ],
  },
  {
    label: t("admin.nav.aiConfig"),
    items: [
      {
        label: t("common.admin.tools"),
        icon: Wrench,
        children: [
          { href: "/admin/tools/mcps", label: t("common.admin.mcps") },
          { href: "/admin/tools/skills", label: t("common.admin.skills") },
          {
            href: "/admin/tools/ai-providers",
            label: t("admin.toolPages.aiProviders"),
          },
          { href: "/admin/tools/models", label: t("admin.toolPages.models") },
          {
            href: "/admin/tools/model-configs",
            label: t("admin.toolPages.modelConfigs"),
          },
        ],
      },
      {
        href: "/admin/mcp-providers",
        icon: Bot,
        label: t("admin.mcpProviders.title"),
      },
      {
        href: "/admin/chat-assistant",
        icon: MessageCircle,
        label: t("admin.chatAssistant.title"),
      },
      {
        href: "/admin/chat-providers",
        icon: MessageCircle,
        label: t("admin.chatProviders.title"),
      },
    ],
  },
  {
    label: t("admin.nav.system"),
    items: [
      {
        href: "/admin/settings",
        icon: Cog,
        label: t("common.admin.settings"),
      },
      {
        href: "/admin/api-keys",
        icon: KeyRound,
        label: t("admin.apiKeys.title"),
      },
    ],
  },
];

interface VersionInfo {
  version: string;
  assemblyVersion: string;
  productName: string;
}

export function AdminSidebar(props: React.ComponentProps<typeof Sidebar>) {
  const pathname = usePathname();
  const t = useTranslations();
  const navGroups = getNavGroups(t);
  const [expandedItems, setExpandedItems] = React.useState<string[]>([
    t("common.admin.tools"),
  ]);
  const [versionInfo, setVersionInfo] = useState<VersionInfo | null>(null);

  useEffect(() => {
    api
      .get<{ success: boolean; data: VersionInfo }>("/api/system/version", {
        skipAuth: true,
      })
      .then((res) => {
        if (res.success) {
          setVersionInfo(res.data);
        }
      })
      .catch(() => {
        // Ignore version fetch failure
      });
  }, []);

  const isPreview = versionInfo?.version?.toLowerCase().includes("preview");
  const displayVersion = versionInfo?.version?.split("+")[0] || "";

  const toggleExpand = (label: string) => {
    setExpandedItems((prev) =>
      prev.includes(label)
        ? prev.filter((item) => item !== label)
        : [...prev, label]
    );
  };

  const isItemActive = (href: string) =>
    href === "/admin"
      ? pathname === href
      : pathname === href || pathname.startsWith(`${href}/`);

  return (
    <Sidebar collapsible="icon" {...props}>
      <SidebarHeader className="border-b">
        <SidebarMenu>
          <SidebarMenuItem>
            <SidebarMenuButton size="lg" asChild tooltip={t("common.adminPanel")}>
              <Link href="/admin">
                <div className="flex aspect-square size-8 items-center justify-center rounded-lg bg-primary text-primary-foreground">
                  <BookOpen className="size-4" />
                </div>
                <div className="grid flex-1 text-left leading-tight">
                  <span className="truncate text-sm font-semibold">
                    {BRAND_NAME}
                  </span>
                  <span className="truncate text-xs text-muted-foreground">
                    {t("common.adminPanel")}
                  </span>
                </div>
              </Link>
            </SidebarMenuButton>
          </SidebarMenuItem>
        </SidebarMenu>
      </SidebarHeader>

      <SidebarContent>
        {navGroups.map((group) => (
          <SidebarGroup key={group.label}>
            <SidebarGroupLabel>{group.label}</SidebarGroupLabel>
            <SidebarGroupContent>
              <SidebarMenu>
                {group.items.map((item) => {
                  if (item.children) {
                    const isExpanded = expandedItems.includes(item.label);
                    const isChildActive = item.children.some((child) =>
                      pathname.startsWith(child.href)
                    );

                    return (
                      <SidebarMenuItem key={item.label}>
                        <SidebarMenuButton
                          tooltip={item.label}
                          isActive={isChildActive}
                          onClick={() => toggleExpand(item.label)}
                        >
                          <item.icon />
                          <span>{item.label}</span>
                          <ChevronRight
                            className={`ml-auto transition-transform ${
                              isExpanded ? "rotate-90" : ""
                            }`}
                          />
                        </SidebarMenuButton>
                        {isExpanded && (
                          <SidebarMenuSub>
                            {item.children.map((child) => (
                              <SidebarMenuSubItem key={child.href}>
                                <SidebarMenuSubButton
                                  asChild
                                  isActive={pathname === child.href}
                                >
                                  <Link href={child.href}>{child.label}</Link>
                                </SidebarMenuSubButton>
                              </SidebarMenuSubItem>
                            ))}
                          </SidebarMenuSub>
                        )}
                      </SidebarMenuItem>
                    );
                  }

                  return (
                    <SidebarMenuItem key={item.href}>
                      <SidebarMenuButton
                        asChild
                        tooltip={item.label}
                        isActive={isItemActive(item.href!)}
                      >
                        <Link href={item.href!}>
                          <item.icon />
                          <span>{item.label}</span>
                        </Link>
                      </SidebarMenuButton>
                    </SidebarMenuItem>
                  );
                })}
              </SidebarMenu>
            </SidebarGroupContent>
          </SidebarGroup>
        ))}
      </SidebarContent>

      <SidebarFooter>
        <SidebarMenu>
          <SidebarMenuItem>
            <SidebarMenuButton asChild tooltip={t("common.backToHome")}>
              <Link href="/">
                <Home />
                <span>{t("common.backToHome")}</span>
              </Link>
            </SidebarMenuButton>
          </SidebarMenuItem>
        </SidebarMenu>
        {displayVersion && (
          <div className="border-t px-3 py-2">
            <div className="flex items-center justify-center gap-2 text-xs text-muted-foreground">
              {isPreview ? (
                <Badge className="border-amber-500/30 bg-amber-500/20 px-2 py-0.5 text-[10px] text-amber-600 hover:bg-amber-500/30 dark:text-amber-400">
                  v{displayVersion}
                </Badge>
              ) : (
                <span>v{displayVersion}</span>
              )}
            </div>
          </div>
        )}
      </SidebarFooter>
      <SidebarRail />
    </Sidebar>
  );
}
