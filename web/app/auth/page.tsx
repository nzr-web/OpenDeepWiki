"use client";

import { useState } from "react";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Separator } from "@/components/ui/separator";
import { Github, Mail, BookOpen, Sparkles, Zap, ArrowLeft, Loader2 } from "lucide-react";
import { useRouter, useSearchParams } from "next/navigation";
import Link from "next/link";
import { useAuth } from "@/contexts/auth-context";
import { useTranslations } from "@/hooks/use-translations";

type AuthMode = "login" | "register";

export default function AuthPage() {
  const router = useRouter();
  const searchParams = useSearchParams();
  const returnUrl = searchParams.get("returnUrl");
  const { login, register } = useAuth();
  const t = useTranslations();
  const [mode, setMode] = useState<AuthMode>("login");
  const [isLoading, setIsLoading] = useState(false);
  const [error, setError] = useState("");

  // Login form
  const [email, setEmail] = useState("");
  const [password, setPassword] = useState("");

  // Register form
  const [name, setName] = useState("");
  const [confirmPassword, setConfirmPassword] = useState("");

  const redirectAfterAuth = () => {
    if (returnUrl) {
      router.push(decodeURIComponent(returnUrl));
    } else {
      router.push("/");
    }
  };

  const handleLogin = async (e: React.FormEvent) => {
    e.preventDefault();
    setError("");
    setIsLoading(true);

    try {
      await login({ email, password });
      redirectAfterAuth();
    } catch (err) {
      setError(err instanceof Error ? err.message : t("auth.errors.loginFailed"));
    } finally {
      setIsLoading(false);
    }
  };

  const handleRegister = async (e: React.FormEvent) => {
    e.preventDefault();
    setError("");

    if (password !== confirmPassword) {
      setError(t("auth.errors.passwordMismatch"));
      return;
    }

    if (password.length < 6) {
      setError(t("auth.errors.passwordTooShort"));
      return;
    }

    setIsLoading(true);

    try {
      await register({ name, email, password, confirmPassword });
      redirectAfterAuth();
    } catch (err) {
      setError(err instanceof Error ? err.message : t("auth.errors.registerFailed"));
    } finally {
      setIsLoading(false);
    }
  };

  const handleOAuthLogin = (provider: string) => {
    // TODO: Implement OAuth login
    console.log(`Login with ${provider}`);
  };

  const switchMode = () => {
    setMode(mode === "login" ? "register" : "login");
    setError("");
  };

  return (
    <div className="flex min-h-screen">
      {/* Left Side - Brand Section */}
      <div className="hidden lg:flex lg:w-1/2 bg-gradient-to-br from-primary/10 via-primary/5 to-background relative overflow-hidden">
        <div className="absolute inset-0 bg-grid-white/5 [mask-image:radial-gradient(white,transparent_85%)]" />

        <div className="relative z-10 flex flex-col justify-between p-12 w-full">
          <div>
            <Link
              href="/"
              className="inline-flex items-center gap-2 text-sm text-muted-foreground hover:text-foreground transition-colors"
            >
              <ArrowLeft className="h-4 w-4" />
              {t("authUi.backToHome")}
            </Link>
          </div>

          <div className="space-y-8">
            <div className="space-y-4">
              <div className="inline-flex items-center gap-2 px-3 py-1 rounded-full bg-primary/10 text-primary text-sm font-medium">
                <Sparkles className="h-4 w-4" />
                {t("authUi.aiPowered")}
              </div>
              <h1 className="text-5xl font-bold tracking-tight">{t("authUi.title")}</h1>
              <p className="text-xl text-muted-foreground max-w-md">
                {t("authUi.subtitle")}
              </p>
            </div>

            <div className="space-y-6 max-w-md">
              <div className="flex items-start gap-4">
                <div className="flex h-10 w-10 shrink-0 items-center justify-center rounded-lg bg-primary/10">
                  <BookOpen className="h-5 w-5 text-primary" />
                </div>
                <div>
                  <h3 className="font-semibold mb-1">{t("authUi.features.smartDocs")}</h3>
                  <p className="text-sm text-muted-foreground">
                    {t("authUi.features.smartDocsDesc")}
                  </p>
                </div>
              </div>

              <div className="flex items-start gap-4">
                <div className="flex h-10 w-10 shrink-0 items-center justify-center rounded-lg bg-primary/10">
                  <Zap className="h-5 w-5 text-primary" />
                </div>
                <div>
                  <h3 className="font-semibold mb-1">{t("authUi.fastSearch")}</h3>
                  <p className="text-sm text-muted-foreground">
                    {t("authUi.features.fastSearchDesc")}
                  </p>
                </div>
              </div>

              <div className="flex items-start gap-4">
                <div className="flex h-10 w-10 shrink-0 items-center justify-center rounded-lg bg-primary/10">
                  <Github className="h-5 w-5 text-primary" />
                </div>
                <div>
                  <h3 className="font-semibold mb-1">{t("authUi.features.githubIntegration")}</h3>
                  <p className="text-sm text-muted-foreground">
                    {t("authUi.features.githubIntegrationDesc")}
                  </p>
                </div>
              </div>
            </div>
          </div>

          <div className="text-sm text-muted-foreground">
            {t("authUi.copyright")}
          </div>
        </div>
      </div>

      {/* Right Side - Login/Register Form */}
      <div className="flex-1 flex items-center justify-center p-8 bg-background">
        <div className="w-full max-w-md space-y-8">
          {/* Mobile Header */}
          <div className="lg:hidden text-center space-y-2">
            <h1 className="text-3xl font-bold">{t("authUi.title")}</h1>
            <p className="text-muted-foreground">{t("authUi.mobileSubtitle")}</p>
          </div>

          <div className="space-y-6">
            <div className="space-y-2 text-center lg:text-left">
              <h2 className="text-2xl font-bold tracking-tight">
                {mode === "login" ? t("authUi.welcome") : t("authUi.createAccount")}
              </h2>
              <p className="text-muted-foreground">
                {mode === "login"
                  ? t("authUi.loginDesc")
                  : t("authUi.registerDesc")}
              </p>
            </div>

            {error && (
              <div className="p-3 text-sm text-red-500 bg-red-50 dark:bg-red-950/50 rounded-md">
                {error}
              </div>
            )}

            {mode === "login" ? (
              <form onSubmit={handleLogin} className="space-y-4">
                <div className="space-y-2">
                  <label htmlFor="email" className="text-sm font-medium">
                    {t("authUi.email")}
                  </label>
                  <Input
                    id="email"
                    type="email"
                    placeholder={t("authUi.emailPlaceholder")}
                    value={email}
                    onChange={(e) => setEmail(e.target.value)}
                    required
                    disabled={isLoading}
                    className="h-11"
                  />
                </div>
                <div className="space-y-2">
                  <div className="flex items-center justify-between">
                    <label htmlFor="password" className="text-sm font-medium">
                      {t("authUi.password")}
                    </label>
                    <Button variant="link" className="p-0 h-auto text-sm">
                      {t("authUi.forgotPassword")}
                    </Button>
                  </div>
                  <Input
                    id="password"
                    type="password"
                    placeholder={t("authUi.passwordPlaceholder")}
                    value={password}
                    onChange={(e) => setPassword(e.target.value)}
                    required
                    disabled={isLoading}
                    className="h-11"
                  />
                </div>
                <Button type="submit" className="w-full h-11" disabled={isLoading}>
                  {isLoading ? (
                    <>
                      <Loader2 className="mr-2 h-4 w-4 animate-spin" />
                      {t("authUi.signingIn")}
                    </>
                  ) : (
                    t("authUi.signIn")
                  )}
                </Button>
              </form>
            ) : (
              <form onSubmit={handleRegister} className="space-y-4">
                <div className="space-y-2">
                  <label htmlFor="name" className="text-sm font-medium">
                    {t("authUi.username")}
                  </label>
                  <Input
                    id="name"
                    type="text"
                    placeholder={t("authUi.usernamePlaceholder")}
                    value={name}
                    onChange={(e) => setName(e.target.value)}
                    required
                    disabled={isLoading}
                    className="h-11"
                    minLength={2}
                    maxLength={50}
                  />
                </div>
                <div className="space-y-2">
                  <label htmlFor="reg-email" className="text-sm font-medium">
                    {t("authUi.email")}
                  </label>
                  <Input
                    id="reg-email"
                    type="email"
                    placeholder={t("authUi.emailPlaceholder")}
                    value={email}
                    onChange={(e) => setEmail(e.target.value)}
                    required
                    disabled={isLoading}
                    className="h-11"
                  />
                </div>
                <div className="space-y-2">
                  <label htmlFor="reg-password" className="text-sm font-medium">
                    {t("authUi.password")}
                  </label>
                  <Input
                    id="reg-password"
                    type="password"
                    placeholder={t("authUi.passwordHint")}
                    value={password}
                    onChange={(e) => setPassword(e.target.value)}
                    required
                    disabled={isLoading}
                    className="h-11"
                    minLength={6}
                  />
                </div>
                <div className="space-y-2">
                  <label htmlFor="confirm-password" className="text-sm font-medium">
                    {t("authUi.confirmPassword")}
                  </label>
                  <Input
                    id="confirm-password"
                    type="password"
                    placeholder={t("authUi.confirmPasswordPlaceholder")}
                    value={confirmPassword}
                    onChange={(e) => setConfirmPassword(e.target.value)}
                    required
                    disabled={isLoading}
                    className="h-11"
                  />
                </div>
                <Button type="submit" className="w-full h-11" disabled={isLoading}>
                  {isLoading ? (
                    <>
                      <Loader2 className="mr-2 h-4 w-4 animate-spin" />
                      {t("authUi.signingUp")}
                    </>
                  ) : (
                    t("authUi.signUp")
                  )}
                </Button>
              </form>
            )}

            <div className="relative">
              <div className="absolute inset-0 flex items-center">
                <Separator />
              </div>
              <div className="relative flex justify-center text-xs uppercase">
                <span className="bg-background px-2 text-muted-foreground">
                  {t("authUi.orThirdParty")}
                </span>
              </div>
            </div>

            <div className="grid grid-cols-2 gap-3">
              <Button
                variant="outline"
                onClick={() => handleOAuthLogin("github")}
                disabled={isLoading}
                className="gap-2 h-11"
              >
                <Github className="h-4 w-4" />
                GitHub
              </Button>
              <Button
                variant="outline"
                onClick={() => handleOAuthLogin("google")}
                disabled={isLoading}
                className="gap-2 h-11"
              >
                <Mail className="h-4 w-4" />
                Google
              </Button>
            </div>

            <div className="text-center text-sm text-muted-foreground">
              {mode === "login" ? t("authUi.noAccount") : t("authUi.hasAccount")}{" "}
              <Button
                variant="link"
                className="p-0 h-auto font-normal text-primary"
                onClick={switchMode}
              >
                {mode === "login" ? t("authUi.signUpNow") : t("authUi.signInNow")}
              </Button>
            </div>

            <div className="lg:hidden text-center">
              <Button variant="ghost" onClick={() => router.push("/")} className="gap-2">
                <ArrowLeft className="h-4 w-4" />
                {t("authUi.backToHome")}
              </Button>
            </div>
          </div>
        </div>
      </div>
    </div>
  );
}
