"use client";

import { useCallback, useState, useTransition } from "react";
import { useRouter } from "next/navigation";
import { AppLayout } from "@/components/app-layout";
import { Input } from "@/components/ui/input";
import { Button } from "@/components/ui/button";
import { Search, Plus, Flame, Puzzle } from "lucide-react";
import { IntegrationsDialog } from "@/components/integrations-dialog";
import { useTranslations } from "@/hooks/use-translations";
import { RepositorySubmitForm } from "@/components/repo/repository-submit-form";
import {
  Dialog,
  DialogContent,
} from "@/components/ui/dialog";
import { useAuth } from "@/contexts/auth-context";
import { useIsAdmin } from "@/hooks/use-is-admin";
import { useScrollPosition } from "@/hooks/use-scroll-position";
import { PublicRepositoryList } from "@/components/repo/public-repository-list";
import { cn } from "@/lib/utils";
import type { LanguageInfo } from "@/lib/recommendation-api";
import type { RepositoryItemResponse } from "@/types/repository";
import { refreshHomePageCache } from "./home-actions";

interface HomePageClientProps {
  data: {
    repositories: RepositoryItemResponse[];
    languages: LanguageInfo[];
    error: boolean;
  };
}

export function HomePageClient({ data }: HomePageClientProps) {
  const t = useTranslations();
  const router = useRouter();
  const { user } = useAuth();
  const isAdmin = useIsAdmin();
  const [activeItem, setActiveItem] = useState(t("sidebar.explore"));
  const [isFormOpen, setIsFormOpen] = useState(false);
  const [isIntegrationsOpen, setIsIntegrationsOpen] = useState(false);
  const [keyword, setKeyword] = useState("");
  const [isRefreshing, startRefresh] = useTransition();
  const { isScrolled } = useScrollPosition(80);

  const refreshHomeData = useCallback(() => {
    startRefresh(() => {
      void refreshHomePageCache().then(() => {
        router.refresh();
      });
    });
  }, [router]);

  const handleSubmitSuccess = useCallback(() => {
    setIsFormOpen(false);
    refreshHomeData();
  }, [refreshHomeData]);

  const handleAddRepoClick = useCallback(() => {
    if (!user) {
      router.push("/auth");
      return;
    }
    setIsFormOpen(true);
  }, [user, router]);

  const handleExploreTrendingClick = useCallback(() => {
    router.push("/recommend?strategy=popular&window=7");
  }, [router]);

  return (
    <AppLayout
      activeItem={activeItem}
      onItemClick={setActiveItem}
      searchBox={{
        value: keyword,
        onChange: setKeyword,
        visible: isScrolled,
      }}
    >
      <div className="flex min-h-0 flex-1 flex-col">
        <section className="relative overflow-hidden border-b border-border/70">
          <div
            aria-hidden
            className="pointer-events-none absolute inset-0 bg-[radial-gradient(ellipse_at_top,color-mix(in_oklab,var(--foreground)_6%,transparent),transparent_58%)]"
          />
          <div className="relative mx-auto flex w-full max-w-[1400px] flex-col gap-5 px-4 py-6 sm:px-6 lg:px-8">
            <div className="flex flex-col gap-4 lg:flex-row lg:items-end lg:justify-between">
              <div className="min-w-0 space-y-1.5">
                <p className="text-[11px] font-medium uppercase tracking-[0.14em] text-muted-foreground">
                  {t("sidebar.explore")}
                </p>
                <h1 className="text-2xl font-semibold tracking-tight text-foreground sm:text-[1.75rem]">
                  {t("home.title")}
                </h1>
                <p className="max-w-xl text-sm text-muted-foreground">
                  {t("home.subtitle")}
                </p>
              </div>

              <div className="flex flex-wrap items-center gap-2">
                {isAdmin && (
                <Dialog open={isFormOpen} onOpenChange={setIsFormOpen}>
                  <Button
                    className="h-9 gap-2 rounded-lg bg-teal-600 px-3.5 text-white hover:bg-teal-500"
                    onClick={handleAddRepoClick}
                  >
                    <Plus className="h-4 w-4" />
                    {t("home.addPrivateRepo")}
                  </Button>
                  <DialogContent className="max-h-[85vh] overflow-y-auto sm:max-w-2xl">
                    {user && (
                      <RepositorySubmitForm onSuccess={handleSubmitSuccess} />
                    )}
                  </DialogContent>
                </Dialog>
                )}
                <Button
                  variant="outline"
                  className="h-9 gap-2 rounded-lg border-border/80 bg-background/60"
                  onClick={handleExploreTrendingClick}
                >
                  <Flame className="h-4 w-4 text-orange-500" />
                  {t("home.exploreTrending")}
                </Button>
                <Button
                  variant="ghost"
                  className="h-9 gap-2 rounded-lg text-muted-foreground hover:text-foreground"
                  onClick={() => setIsIntegrationsOpen(true)}
                >
                  <Puzzle className="h-4 w-4" />
                  <span className="hidden sm:inline">{t("home.mcpIntegrationShort")}</span>
                  <span className="sm:hidden">MCP</span>
                </Button>
              </div>
            </div>

            <div
              className={cn(
                "relative max-w-3xl transition-all duration-250 ease-out",
                isScrolled
                  ? "pointer-events-none -translate-y-1 opacity-0"
                  : "translate-y-0 opacity-100"
              )}
            >
              <Search className="pointer-events-none absolute top-1/2 left-3.5 h-4 w-4 -translate-y-1/2 text-muted-foreground" />
              <Input
                value={keyword}
                onChange={(e) => setKeyword(e.target.value)}
                placeholder={t("home.searchPlaceholder")}
                maxLength={100}
                className="h-11 rounded-xl border-border/70 bg-background/80 pl-10 text-sm shadow-none transition-[border-color,box-shadow] focus-visible:border-teal-500/40 focus-visible:ring-teal-500/15"
              />
            </div>
          </div>

          <IntegrationsDialog
            open={isIntegrationsOpen}
            onOpenChange={setIsIntegrationsOpen}
          />
        </section>

        <section className="mx-auto w-full max-w-[1400px] flex-1 px-4 py-5 sm:px-6 lg:px-8">
          <PublicRepositoryList
            keyword={keyword}
            repositories={data.repositories}
            languages={data.languages}
            loadError={data.error}
            refreshing={isRefreshing}
            onRefresh={refreshHomeData}
          />
        </section>
      </div>
    </AppLayout>
  );
}
