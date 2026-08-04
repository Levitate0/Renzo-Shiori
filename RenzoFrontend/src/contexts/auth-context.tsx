'use client';

import React, { createContext, useContext, useState, useEffect, useCallback } from 'react';
import { useRouter } from 'next/navigation';
import { SESSION_EXPIRED_EVENT } from '@/lib/api/client';
import { userService } from '@/lib/api/services/userService';
import { ensureImageToken } from '@/lib/api/imageToken';
import { type User, type AuthStatus, UserLevel } from '@/lib/api/types';

// Cookie helpers for user session persistence (works across tabs/windows)
function setSessionCookie(username: string): void {
  // Expires in 30 days
  const expires = new Date(Date.now() + 30 * 24 * 60 * 60 * 1000).toUTCString();
  document.cookie = `renzo_user_session=${encodeURIComponent(username)}; expires=${expires}; path=/; SameSite=Lax`;
}

function clearSessionCookie(): void {
  document.cookie = 'renzo_user_session=; expires=Thu, 01 Jan 1970 00:00:00 UTC; path=/; SameSite=Lax';
}

function getSessionCookie(): string | null {
  const match = document.cookie.match(/(?:^|;\s*)renzo_user_session=([^;]*)/);
  return match?.[1] ? decodeURIComponent(match[1]) : null;
}

interface AuthContextType {
  user: User | null;
  isAuthenticated: boolean;
  isLoading: boolean;
  isAuthEnabled: boolean;
  availableUsers: AuthStatus['users'];
  userLevel: UserLevel;
  canManage: boolean;      // Manager+ (can manage series, providers, sources)
  canAdmin: boolean;       // Admin/Owner (can manage users, settings, delete, clear alerts)
  canOwner: boolean;       // Owner only (can edit settings, manage other admins)
  login: (username: string, password: string, rememberMe?: boolean) => Promise<void>;
  selectUser: (username: string) => Promise<void>;
  logout: () => Promise<void>;
  changePassword: (currentPassword: string, newPassword: string) => Promise<void>;
  refreshAuth: () => Promise<void>;
  setAuthFromToken: (token: string, user: User) => void;
}

const AuthContext = createContext<AuthContextType | undefined>(undefined);

export function AuthProvider({ children }: { children: React.ReactNode }) {
  const router = useRouter();
  const [user, setUser] = useState<User | null>(null);
  const [token, setToken] = useState<string | null>(null);
  const [selectedUsername, setSelectedUsername] = useState<string | null>(null);
  const [isAuthEnabled, setIsAuthEnabled] = useState(false);
  const [availableUsers, setAvailableUsers] = useState<AuthStatus['users']>([]);
  const [isLoading, setIsLoading] = useState(true);

  // Initialize - check auth status on mount
  const refreshAuth = useCallback(async () => {
    try {
      const status = await userService.getAuthStatus();
      setIsAuthEnabled(status.authenticationEnabled);
      setAvailableUsers(status.users ?? []);

      if (status.authenticationEnabled) {
        // Try to load stored token
        // Token is stored in memory; on page refresh try refresh token
        const storedToken = sessionStorage.getItem('renzo_token');
        if (storedToken) {
          setToken(storedToken);
          try {
            const me = await userService.getMe();
            setUser(me);
            return;
          } catch {
            // Token expired, try refresh
            sessionStorage.removeItem('renzo_token');
            setToken(null);
          }
        }

        // Try refresh token (cookie-based)
        try {
          const refreshResult = await userService.refreshToken();
          setToken(refreshResult.token);
          sessionStorage.setItem('renzo_token', refreshResult.token);
          setUser(refreshResult.user);
          return;
        } catch {
          // No refresh token or expired
          setUser(null);
          setToken(null);
        }
      } else {
        // Auth disabled - determine if we should auto-login or show user-select
        const userCount = status.users?.length ?? 0;

        // Try localStorage first, then cookie fallback for stored session
        let storedUsername = localStorage.getItem('renzo_selected_user');
        if (!storedUsername) {
          storedUsername = getSessionCookie();
        }

        if (storedUsername) {
          setSelectedUsername(storedUsername);
          try {
            const selectedUser = await userService.selectUser(storedUsername);
            // Sync both storage mechanisms
            localStorage.setItem('renzo_selected_user', storedUsername);
            setSessionCookie(storedUsername);
            setUser(selectedUser);
            return;
          } catch {
            // Stored user no longer valid — clear and fall through
            localStorage.removeItem('renzo_selected_user');
            clearSessionCookie();
            setSelectedUsername(null);
          }
        }

        // No stored username or auto-login failed
        if (userCount > 1) {
          // Multiple users and no valid stored session — show user selection
          setUser(null);
          setAvailableUsers(status.users ?? []);
          return;
        }

        // Exactly one user — auto-login
        if (userCount === 1) {
          const singleUser = status.users![0]!;
          try {
            const selectedUser = await userService.selectUser(singleUser.username);
            localStorage.setItem('renzo_selected_user', singleUser.username);
            setSessionCookie(singleUser.username);
            setSelectedUsername(singleUser.username);
            setUser(selectedUser);
            return;
          } catch {
            // Auto-login failed, fall through to setUser(null)
          }
        }

        setUser(null);
      }
    } catch {
      // Offline or error
      setUser(null);
    } finally {
      setIsLoading(false);
    }
  }, []);

  // After auth check completes, redirect unauthenticated users to the correct page
  // Only applies to non-auth pages (avoids redirect loops on /login, /user-select)
  useEffect(() => {
    if (!isLoading && !user) {
      const pathname = window.location.pathname;
      // Don't redirect if already on an auth-related page
      if (pathname === '/login' || pathname === '/user-select' || pathname.startsWith('/auth/')) {
        return;
      }

      if (isAuthEnabled) {
        router.push('/login');
      } else if (availableUsers && availableUsers.length > 0) {
        // Auth disabled with users — show user selector
        router.push('/user-select');
      }
      // Auth disabled with no users — no redirect here; page.tsx handles it
      // using wizard completion state to decide between /users or /library
    }
  }, [isLoading, user, isAuthEnabled, availableUsers, router]);

  useEffect(() => {
    refreshAuth();
  }, [refreshAuth]);

  // The API client announces a session it can no longer refresh (expired or
  // revoked). Clear the user so the redirect effect above sends us to the login
  // screen — otherwise every page keeps rendering with no data and no
  // explanation, which reads as "the app is broken" rather than "sign in
  // again". No logout call: the session is already gone server-side.
  useEffect(() => {
    const onExpired = () => {
      setToken(null);
      try {
        sessionStorage.removeItem('renzo_token');
        localStorage.removeItem('renzo_selected_user');
      } catch { /* private mode */ }
      clearSessionCookie();
      setSelectedUsername(null);
      setUser(null);
      setIsLoading(false);
    };
    window.addEventListener(SESSION_EXPIRED_EVENT, onExpired);
    return () => window.removeEventListener(SESSION_EXPIRED_EVENT, onExpired);
  }, []);

  // Eagerly fetch the short-lived image-scoped token (see imageToken.ts) as soon
  // as we have a valid session, rather than waiting for the first <img> tag to
  // lazily trigger it on render. Without this, the very first page a user lands
  // on after login/refresh (e.g. a browse grid with dozens of covers) mounts all
  // its <img> tags before any image token exists yet - every one of them fires
  // its network request untokened and gets 401'd, and nothing re-renders them
  // once the token arrives a moment later, since React has no reason to know a
  // background fetch resolved. Covers the login, selectUser, refreshAuth
  // (stored-token and refresh-token paths), and setAuthFromToken flows in one
  // place, since they all converge on setUser().
  useEffect(() => {
    if (user) {
      void ensureImageToken();
    }
  }, [user]);

  const login = useCallback(async (username: string, password: string, rememberMe = false) => {
    const result = await userService.login({ username, password, rememberMe });
    // Clean up any stale auth-disabled user storage
    localStorage.removeItem('renzo_selected_user');
    setSelectedUsername(null);
    setToken(result.token);
    sessionStorage.setItem('renzo_token', result.token);
    setUser(result.user);
    // Persist user session via cookie (backed by httpOnly refresh_token when rememberMe=true)
    setSessionCookie(username);
    router.push('/library');
  }, [router]);

  const selectUser = useCallback(async (username: string) => {
    const result = await userService.selectUser(username);
    localStorage.setItem('renzo_selected_user', username);
    setSessionCookie(username);
    setSelectedUsername(username);
    setUser(result);
    router.push('/library');
  }, [router]);

  const logout = useCallback(async () => {
    // Fire the logout API call as fire-and-forget so client-side cleanup is instant
    userService.logout().catch(() => {});
    setToken(null);
    sessionStorage.removeItem('renzo_token');
    localStorage.removeItem('renzo_selected_user');
    clearSessionCookie();
    setSelectedUsername(null);
    setUser(null);
    router.push(isAuthEnabled ? '/login' : '/user-select');
  }, [router, isAuthEnabled]);

  const changePassword = useCallback(async (currentPassword: string, newPassword: string) => {
    await userService.changePassword({ currentPassword, newPassword });
  }, []);

  // Directly set auth state from a token+user pair (used by set-password page)
  const setAuthFromToken = useCallback((token: string, userData: User) => {
    setToken(token);
    sessionStorage.setItem('renzo_token', token);
    setUser(userData);
    setSessionCookie(userData.username);
  }, []);

  // Permission helpers
  // Default to Owner level when no user is set (guest mode) so all UI is visible
  const userLevel = user?.level ?? UserLevel.Owner;
  const canManage = userLevel >= UserLevel.Manager;
  const canAdmin = userLevel >= UserLevel.Admin;
  const canOwner = userLevel >= UserLevel.Owner;

  return (
    <AuthContext.Provider
      value={{
        user,
        isAuthenticated: !!user,
        isLoading,
        isAuthEnabled,
        availableUsers,
        userLevel,
        canManage,
        canAdmin,
        canOwner,
        login,
        selectUser,
        logout,
        changePassword,
        refreshAuth,
        setAuthFromToken,
      }}
    >
      {children}
    </AuthContext.Provider>
  );
}

export function useAuth() {
  const context = useContext(AuthContext);
  if (context === undefined) {
    throw new Error('useAuth must be used within an AuthProvider');
  }
  return context;
}