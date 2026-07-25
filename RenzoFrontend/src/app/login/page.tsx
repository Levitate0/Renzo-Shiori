'use client';

import { useState, useEffect } from 'react';
import Link from 'next/link';
import { useAuth } from '@/contexts/auth-context';
import { Button } from '@/components/ui/button';
import { Input } from '@/components/ui/input';
import { Label } from '@/components/ui/label';
import { Checkbox } from '@/components/ui/checkbox';
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from '@/components/ui/card';

const REMEMBERED_USER_KEY = 'renzo_remembered_username';

export default function LoginPage() {
  const { login, isAuthEnabled } = useAuth();
  const [username, setUsername] = useState('');
  const [password, setPassword] = useState('');
  // Default on: staying signed in until explicit logout is the expected behavior
  // for a personal/installed (PWA) app; unchecking opts into a 24h session.
  const [rememberMe, setRememberMe] = useState(true);
  const [error, setError] = useState('');
  const [loading, setLoading] = useState(false);
  const [resetDone, setResetDone] = useState(false);

  // On mount, pre-fill username from localStorage if previously remembered,
  // and show a confirmation note when arriving from a completed password reset.
  useEffect(() => {
    const rememberedUsername = localStorage.getItem(REMEMBERED_USER_KEY);
    if (rememberedUsername) {
      setUsername(rememberedUsername);
      setRememberMe(true);
    }
    setResetDone(new URLSearchParams(window.location.search).get('reset') === '1');
  }, []);

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault();
    setError('');
    setLoading(true);

    try {
      await login(username, password, rememberMe);
      // Persist or clear the remembered username based on checkbox state
      if (rememberMe) {
        localStorage.setItem(REMEMBERED_USER_KEY, username);
      } else {
        localStorage.removeItem(REMEMBERED_USER_KEY);
      }
    } catch (err) {
      setError(err instanceof Error ? err.message : 'Login failed');
    } finally {
      setLoading(false);
    }
  };

  // If auth is disabled, redirect is handled by layout
  if (!isAuthEnabled) {
    return (
      <div className="flex items-center justify-center min-h-screen">
        <Card className="w-full max-w-md">
          <CardHeader>
            <CardTitle>Authentication Disabled</CardTitle>
            <CardDescription>
              Authentication is not enabled. Please go back to the user selection page.
            </CardDescription>
          </CardHeader>
          <CardContent>
            <Button onClick={() => window.location.href = '/user-select'} className="w-full">
              Go to User Selection
            </Button>
          </CardContent>
        </Card>
      </div>
    );
  }

  return (
    <div className="flex items-center justify-center min-h-screen bg-background">
      <Card className="w-full max-w-md mx-4">
        <CardHeader className="space-y-3">
          <CardTitle className="flex justify-center">
            {/* eslint-disable-next-line @next/next/no-img-element */}
            <img src="/renzo-login-banner-light.png?v=shiori" alt="Renzo Shiori" className="block dark:hidden w-64 max-w-full h-auto" />
            {/* eslint-disable-next-line @next/next/no-img-element */}
            <img src="/renzo-login-banner-dark.png?v=shiori" alt="Renzo Shiori" aria-hidden="true" className="hidden dark:block w-64 max-w-full h-auto" />
          </CardTitle>
          <CardDescription className="text-center">
            Enter your credentials to log in
          </CardDescription>
        </CardHeader>
        <CardContent>
          <form onSubmit={handleSubmit} className="space-y-4">
            {resetDone && !error && (
              <div className="p-3 text-sm rounded-md bg-muted text-muted-foreground">
                Password reset successful. Log in with your new password.
              </div>
            )}
            {error && (
              <div className="p-3 text-sm text-red-500 bg-red-50 dark:bg-red-950 rounded-md">
                {error}
              </div>
            )}
            <div className="space-y-2">
              <Label htmlFor="username">Username</Label>
              <Input
                id="username"
                type="text"
                placeholder="Enter your username"
                value={username}
                onChange={(e) => setUsername(e.target.value)}
                required
                autoFocus
              />
            </div>
            <div className="space-y-2">
              <Label htmlFor="password">Password</Label>
              <Input
                id="password"
                type="password"
                placeholder="Enter your password"
                value={password}
                onChange={(e) => setPassword(e.target.value)}
                required
              />
            </div>
            <div className="flex items-center space-x-2">
              <Checkbox
                id="rememberMe"
                checked={rememberMe}
                onCheckedChange={(checked) => setRememberMe(checked === true)}
              />
              <Label htmlFor="rememberMe" className="text-sm cursor-pointer">
                Remember me
              </Label>
            </div>
            <Button type="submit" className="w-full" disabled={loading}>
              {loading ? 'Logging in...' : 'Log in'}
            </Button>
            <div className="text-center text-sm">
              <Link
                href="/auth/forgot-password"
                className="text-muted-foreground hover:text-foreground underline-offset-4 hover:underline"
              >
                Forgot password?
              </Link>
            </div>
          </form>
        </CardContent>
      </Card>
    </div>
  );
}