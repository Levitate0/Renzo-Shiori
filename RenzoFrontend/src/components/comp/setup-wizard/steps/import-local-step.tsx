"use client";

import React, { useState, useEffect, useCallback, useRef } from 'react';
import { CheckCircle, AlertCircle, Circle } from "lucide-react";
import {
  useSetupWizardScanLocalFiles,
  useSetupWizardInstallExtensions,
  useSetupWizardSearchSeries,
  useSetupWizardStatus,
  useSignalRProgress
} from "@/lib/api/hooks/useSetupWizard";
import { JobType, ProgressStatus, type ProgressState, type SetupJobStatusValue, type SetupJobsStatus } from "@/lib/api/types";

// Module-level (not recreated per render) so it's a stable reference for hook dependencies
// and array-index lookups keyed by currentActionIndex.
const WATCHED_JOB_TYPES: JobType[] = [JobType.ScanLocalFiles, JobType.InstallAdditionalExtensions, JobType.SearchProviders];

const isJobStatusCompleted = (s: SetupJobStatusValue) => s === 'Completed';
const isJobStatusInFlight = (s: SetupJobStatusValue) => s === 'Running' || s === 'Waiting';

// Custom hook to detect if scrollbar is visible
function useScrollbarDetection() {
  const [hasScrollbar, setHasScrollbar] = useState(false);
  const containerRef = useRef<HTMLDivElement>(null);
  const timeoutRef = useRef<NodeJS.Timeout>(null);

  useEffect(() => {
    const checkScrollbar = () => {
      if (containerRef.current) {
        const { scrollHeight, clientHeight } = containerRef.current;
        const newHasScrollbar = scrollHeight > clientHeight;
        setHasScrollbar(prev => prev !== newHasScrollbar ? newHasScrollbar : prev);
      }
    };

    const debouncedCheck = () => {
      if (timeoutRef.current) {
        clearTimeout(timeoutRef.current);
      }
      timeoutRef.current = setTimeout(checkScrollbar, 100);
    };

    checkScrollbar();

    const observer = new ResizeObserver(debouncedCheck);
    if (containerRef.current) {
      observer.observe(containerRef.current);
    }

    return () => {
      observer.disconnect();
      if (timeoutRef.current) {
        clearTimeout(timeoutRef.current);
      }
    };
  }, []);

  return { hasScrollbar, containerRef };
}

interface ImportLocalStepProps {
  setError: (error: string | null) => void;
  setIsLoading: (loading: boolean) => void;
  setCanProgress: (canProgress: boolean) => void;
  /** Called once the scan/import process has started (or was found already running). */
  onProcessStarted?: () => void;
  /**
   * Import wizard mode: always run a fresh scan instead of treating a previous run's
   * completed jobs as "already done". A scan that is genuinely in-flight (e.g. the page
   * was reloaded mid-scan) is still resumed rather than restarted.
   */
  forceRescan?: boolean;
  /**
   * Scan the configured ImportFolder instead of StorageFolder, registering bare titles for
   * archive-less folders (e.g. a Suwayomi migration) instead of skipping them.
   */
  titleOnly?: boolean;
}

interface ActionProgressProps {
  title: string;
  isActive: boolean;
  isCompleted: boolean;
  isFailed: boolean;
  progress: number;
  message?: string;
  errorMessage?: string;
}

function ActionProgress({
  title,
  isActive,
  isCompleted,
  isFailed,
  progress,
  message,
  errorMessage
}: ActionProgressProps) {
  const cardClass = [
    'iw-scan-card',
    isActive    ? 'is-active' : '',
    isCompleted ? 'is-done'   : '',
    isFailed    ? 'is-failed' : '',
  ].filter(Boolean).join(' ');

  const iconClass = [
    'iw-scan-icon',
    isFailed    ? 'is-failed'  : '',
    isCompleted ? 'is-done'    : '',
    isActive    ? 'is-spinning': '',
    (!isFailed && !isCompleted && !isActive) ? 'is-idle' : '',
  ].filter(Boolean).join(' ');

  const renderIcon = () => {
    if (isFailed)    return <AlertCircle />;
    if (isCompleted) return <CheckCircle />;
    if (isActive)    return null; // spinner via CSS border animation
    return <Circle />;
  };

  // One decimal while running: long jobs (e.g. searching hundreds of titles)
  // advance in sub-1% steps that integer rounding would hide entirely.
  const pctLabel = isCompleted
    ? '100%'
    : isActive || isFailed
      ? `${(Math.round(progress * 10) / 10).toFixed(1)}%`
      : '—';

  const statusText = message ?? (isCompleted ? 'Complete' : isActive ? 'Running…' : 'Queued');

  return (
    <div className={cardClass}>
      <div className={iconClass}>
        {renderIcon()}
      </div>

      <div className="iw-scan-body">
        <div className="iw-scan-label">{title}</div>
        <div className="iw-progress-bar">
          <div
            className="iw-progress-fill"
            style={{ width: `${isCompleted ? 100 : progress}%` }}
          />
        </div>
        <div className="iw-scan-status">{statusText}</div>
        {errorMessage && (
          <div className="iw-scan-error">{errorMessage}</div>
        )}
      </div>

      <div className={`iw-scan-pct${(!isActive && !isCompleted && !isFailed) ? ' is-muted' : ''}`}>
        {pctLabel}
      </div>
    </div>
  );
}

export function ImportLocalStep({ setError, setIsLoading, setCanProgress, onProcessStarted, forceRescan, titleOnly }: ImportLocalStepProps) {
  const [currentActionIndex, setCurrentActionIndex] = useState(-1);
  const [allActionsCompleted, setAllActionsCompleted] = useState(false);
  // Jobs that the server reports already completed (e.g. before a page reload) so the UI
  // shows them done instead of resetting to 0% (SignalR has no history after a reload).
  const [serverCompleted, setServerCompleted] = useState<Set<JobType>>(new Set());
  // Server-side last-known progress per job, refreshed by the fallback status poll.
  // Covers SignalR gaps (blocked connection, missed events after reconnect): the cards
  // then still show percentage/message, just at the poll cadence instead of live.
  const [polledProgress, setPolledProgress] = useState<Partial<Record<JobType, ProgressState>>>({});
  const { hasScrollbar, containerRef } = useScrollbarDetection();

  // Use only refs for duplicate prevention - no state
  const completedJobsRef = useRef<Set<JobType>>(new Set());
  const processingJobRef = useRef<JobType | null>(null);
  const hasStartedRef = useRef(false);

  const scanMutation = useSetupWizardScanLocalFiles();
  const installMutation = useSetupWizardInstallExtensions();
  const searchMutation = useSetupWizardSearchSeries();
  const statusMutation = useSetupWizardStatus();

  const handleJobComplete = useCallback((jobType: JobType) => {
    // Check if we've already processed this job completion using ref
    if (completedJobsRef.current.has(jobType)) {
      return;
    }

    // Check if we're currently processing this job (prevents race conditions) using ref
    if (processingJobRef.current === jobType) {
      return;
    }

    processingJobRef.current = jobType;

    // Add to completed jobs immediately to prevent duplicates
    completedJobsRef.current.add(jobType);

    // Trigger next action based on job type immediately without timeout
    const triggerNextAction = async () => {
      try {
        if (jobType === JobType.ScanLocalFiles) {
          setCurrentActionIndex(1);
          await installMutation.mutateAsync();
        } else if (jobType === JobType.InstallAdditionalExtensions) {
          setCurrentActionIndex(2);
          await searchMutation.mutateAsync();
        } else if (jobType === JobType.SearchProviders) {
          setAllActionsCompleted(true);
          setCurrentActionIndex(-1);
        }
      } catch (error) {
        console.error(`Failed to trigger next action after ${jobType}:`, error);
        setError(`Failed to continue after ${jobType === JobType.ScanLocalFiles ? 'scan' : jobType === JobType.InstallAdditionalExtensions ? 'install' : 'search'}`);
        setCurrentActionIndex(-1);
      } finally {
        processingJobRef.current = null;
      }
    };

    // Execute immediately without timeout to reduce race conditions
    void triggerNextAction();
  }, [installMutation, searchMutation, setError]);

  const handleJobError = useCallback((error: string, jobType: JobType) => {
    console.error(`Job failed: ${jobType}`, error);
    setError(`Action failed: ${error}`);
    setCurrentActionIndex(-1);
    processingJobRef.current = null;
  }, [setError]);

  const { getProgressForJob, isJobCompleted, isJobFailed, getJobProgress } = useSignalRProgress({
    jobTypes: WATCHED_JOB_TYPES,
    onComplete: handleJobComplete,
    onError: handleJobError,
  });

  // Fallback for a missed SignalR broadcast (no replay, and the connection handshake can still
  // lose the very first message if a job finishes before it completes) — poll server-side status
  // periodically while an action is active so this can't strand the wizard at 0% forever.
  useEffect(() => {
    if (currentActionIndex < 0) return;
    const activeJobType = WATCHED_JOB_TYPES[currentActionIndex];
    if (activeJobType === undefined) return;

    const interval = setInterval(() => {
      statusMutation.mutateAsync()
        .then((status: SetupJobsStatus) => {
          const value =
            activeJobType === JobType.ScanLocalFiles ? status.scanLocalFiles :
            activeJobType === JobType.InstallAdditionalExtensions ? status.installAdditionalExtensions :
            status.searchProviders;

          // Fallback progress: keep the cards moving even when this client's
          // SignalR connection is dead or missed events. Terminal snapshots
          // (Completed/Failed) are skipped — they can be leftovers from a
          // previous run and would pin a fresh run's percentage at 100.
          const polled: Partial<Record<JobType, ProgressState>> = {};
          const isLive = (p: ProgressState | null | undefined): p is ProgressState =>
            !!p && (p.progressStatus === ProgressStatus.Started || p.progressStatus === ProgressStatus.InProgress);
          if (isLive(status.scanLocalFilesProgress)) polled[JobType.ScanLocalFiles] = status.scanLocalFilesProgress;
          if (isLive(status.installAdditionalExtensionsProgress)) polled[JobType.InstallAdditionalExtensions] = status.installAdditionalExtensionsProgress;
          if (isLive(status.searchProvidersProgress)) polled[JobType.SearchProviders] = status.searchProvidersProgress;
          setPolledProgress(polled);

          if (value === 'Completed') {
            handleJobComplete(activeJobType);
          } else if (value === 'Failed') {
            handleJobError('Job failed', activeJobType);
          }
        })
        .catch(() => { /* transient — next tick retries */ });
    }, 4000);

    return () => clearInterval(interval);
  }, [currentActionIndex, statusMutation, handleJobComplete, handleJobError]);

  const actions = [
    {
      title: "Scan Local Files",
      jobType: JobType.ScanLocalFiles,
    },
    {
      title: "Install Additional Sources",
      jobType: JobType.InstallAdditionalExtensions,
    },
    {
      title: "Search Series",
      jobType: JobType.SearchProviders,
    },
  ];

  /**
   * If the server reports a scan/install/search job in flight (e.g. another
   * session — or this one before a reload — already started the pipeline),
   * mark the earlier steps done and attach to the running one so this wizard
   * session syncs with the live run instead of trying to start a new one.
   * Returns true when it attached.
   */
  const attachToInFlight = useCallback((status: { scanLocalFiles: SetupJobStatusValue; installAdditionalExtensions: SetupJobStatusValue; searchProviders: SetupJobStatusValue }): boolean => {
    const order: { index: number; status: SetupJobStatusValue }[] = [
      { index: 0, status: status.scanLocalFiles },
      { index: 1, status: status.installAdditionalExtensions },
      { index: 2, status: status.searchProviders },
    ];
    const inFlight = order.find((a) => isJobStatusInFlight(a.status));
    if (!inFlight) return false;

    const completed = new Set<JobType>();
    for (const a of order) {
      if (a.index < inFlight.index && isJobStatusCompleted(a.status)) {
        completed.add(WATCHED_JOB_TYPES[a.index]!);
        completedJobsRef.current.add(WATCHED_JOB_TYPES[a.index]!);
      }
    }
    if (completed.size > 0) setServerCompleted((prev) => new Set([...prev, ...completed]));
    setError(null);
    setCurrentActionIndex(inFlight.index);
    return true;
  }, [setError]);

  const triggerAction = useCallback((index: number) => {
    setCurrentActionIndex(index);
    const label = index === 0 ? 'scan' : index === 1 ? 'install' : 'search';
    const promise = index === 0
      ? scanMutation.mutateAsync(titleOnly ?? false)
      : index === 1
        ? installMutation.mutateAsync()
        : searchMutation.mutateAsync();
    promise.catch(async (error) => {
      console.error(`Failed to start ${label}:`, error);
      // Before surfacing a failure, re-check the server: the start call can
      // fail because a pipeline is already live from another session, or
      // because the server was briefly restarting. Attach to whatever is
      // actually running rather than stranding the wizard on an error.
      try {
        const status = await statusMutation.mutateAsync();
        if (attachToInFlight(status)) return;
      } catch { /* server still unreachable — fall through to the error */ }
      setError(`Failed to start ${label} process`);
      setCurrentActionIndex(-1);
      hasStartedRef.current = false; // Reset on error to allow retry
    });
  }, [scanMutation, installMutation, searchMutation, statusMutation, attachToInFlight, titleOnly, setError]);

  // On mount, reconcile with the server so a page reload resumes the running step
  // (and a wizard opened while another session's pipeline is live attaches to it)
  // instead of restarting the whole scan/install/search chain from scratch.
  useEffect(() => {
    if (hasStartedRef.current) return;
    hasStartedRef.current = true;
    setError(null);

    // The status call can fail transiently (server redeploying, network blip).
    // Retry a few times before giving up — blindly starting a scan on failure
    // used to strand the wizard on "Failed to start scan process".
    const fetchStatusWithRetry = async (retries = 4, delayMs = 3000) => {
      for (let attempt = 0; ; attempt++) {
        try {
          return await statusMutation.mutateAsync();
        } catch (e) {
          if (attempt >= retries) throw e;
          await new Promise((resolve) => setTimeout(resolve, delayMs));
        }
      }
    };

    fetchStatusWithRetry()
      .then((status) => {
        onProcessStarted?.();

        // A live pipeline (from this or any other session) takes priority:
        // sync to it rather than starting anything new.
        if (attachToInFlight(status)) return;

        // Import wizard: ignore stale "Completed" statuses left over from a previous
        // import run and kick off a fresh scan from the beginning.
        if (forceRescan) {
          triggerAction(0);
          return;
        }

        const order: { index: number; status: SetupJobStatusValue }[] = [
          { index: 0, status: status.scanLocalFiles },
          { index: 1, status: status.installAdditionalExtensions },
          { index: 2, status: status.searchProviders },
        ];

        // Setup wizard: resume completed jobs across a page reload.
        // Mark already-completed jobs as done (so they don't show 0%).
        const completed = new Set<JobType>();
        for (const a of order) {
          if (isJobStatusCompleted(a.status)) {
            completed.add(actions[a.index]!.jobType);
            completedJobsRef.current.add(actions[a.index]!.jobType);
          }
        }
        if (completed.size > 0) setServerCompleted(completed);

        const firstIncomplete = order.find((a) => !isJobStatusCompleted(a.status));
        if (!firstIncomplete) {
          // Everything already finished.
          setAllActionsCompleted(true);
          setCurrentActionIndex(-1);
          return;
        }

        // Not started (or previously failed) - (re)start from this step.
        // (In-flight jobs were already handled by attachToInFlight above.)
        triggerAction(firstIncomplete.index);
      })
      .catch(() => {
        onProcessStarted?.();
        setError('Could not reach the server to check import status. Close the wizard and try again.');
        hasStartedRef.current = false; // allow a reopened wizard to retry
      });
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []); // Empty dependency array - only run once on mount

  // Update loading and progress states
  useEffect(() => {
    const isAnyActionRunning = currentActionIndex >= 0;
    setIsLoading(isAnyActionRunning);
    setCanProgress(allActionsCompleted);
  }, [currentActionIndex, allActionsCompleted, setIsLoading, setCanProgress]);

  return (
    <div className="space-y-4">
      <p className="text-sm" style={{ color: 'hsl(var(--muted-foreground))' }}>
        Scanning local files, installing sources, and searching for series matches. All actions
        run automatically — this may take a few minutes depending on the number of series and sources.
      </p>

      <div
        ref={containerRef}
        className={`p-0.5 ${hasScrollbar ? 'pr-2' : ''}`}
      >
        <div className="space-y-3">
          {actions.map((action, index) => {
            const isCompleted = isJobCompleted(action.jobType) || serverCompleted.has(action.jobType);
            const isActive = currentActionIndex === index && !isCompleted;
            const isFailed = isJobFailed(action.jobType);
            // Live SignalR data when available; otherwise the server-side
            // snapshot from the fallback status poll.
            const progressData = getProgressForJob(action.jobType) ?? polledProgress[action.jobType] ?? null;
            const progress = isCompleted
              ? 100
              : Math.max(getJobProgress(action.jobType), polledProgress[action.jobType]?.percentage ?? 0);

            return (
              <ActionProgress
                key={action.jobType}
                title={action.title}
                isActive={isActive}
                isCompleted={isCompleted}
                isFailed={isFailed}
                progress={progress}
                message={progressData?.message}
                errorMessage={progressData?.errorMessage}
              />
            );
          })}

          {allActionsCompleted && (
            <div className="iw-done-banner" style={{ marginTop: '4px' }}>
              <CheckCircle />
              <span>Series process completed successfully</span>
            </div>
          )}
        </div>
      </div>
    </div>
  );
}
