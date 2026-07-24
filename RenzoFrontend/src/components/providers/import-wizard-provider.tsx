"use client";

import React, { createContext, useContext, useEffect, useState, useCallback, useMemo } from 'react';
import { useQueryClient } from '@tanstack/react-query';

interface ImportWizardState {
  isActive: boolean;
  currentStep: number;
  completedSteps: number;
  stepData: Record<string, unknown>;
  /** True when this run scans the configured ImportFolder (e.g. a Suwayomi migration)
   * instead of StorageFolder, registering bare titles for archive-less folders. */
  titleOnly: boolean;
}

interface ImportWizardContextType {
  isWizardActive: boolean;
  currentStep: number;
  totalSteps: number;
  isLoading: boolean;
  titleOnly: boolean;
  nextStep: () => void;
  previousStep: () => void;
  completeWizard: () => void;
  startWizard: (titleOnly?: boolean) => void;
  cancelWizard: () => void;
  setStepData: (stepIndex: number, data: unknown) => void;
  getStepData: (stepIndex: number) => unknown;
}

const IMPORT_WIZARD_STORAGE_KEY = 'import-wizard-state';
const TOTAL_STEPS = 4; // import, confirm, schedule, finish

const ImportWizardContext = createContext<ImportWizardContextType | undefined>(undefined);

export function ImportWizardProvider({ children }: { children: React.ReactNode }) {
  const queryClient = useQueryClient();
  
  const [wizardState, setWizardState] = useState<ImportWizardState>({
    isActive: false,
    currentStep: 0,
    completedSteps: 0,
    stepData: {},
    titleOnly: false,
  });
  
  const [isClient, setIsClient] = useState(false);

  // Initialize client-side state after hydration
  useEffect(() => {
    setIsClient(true);
    const saved = localStorage.getItem(IMPORT_WIZARD_STORAGE_KEY);
    if (saved) {
      try {
        const parsedState = JSON.parse(saved) as ImportWizardState;
        setWizardState(parsedState);
      } catch {
        // If parsing fails, keep default state
      }
    }
  }, []);

  // Save state to localStorage whenever it changes
  useEffect(() => {
    if (typeof window !== 'undefined') {
      localStorage.setItem(IMPORT_WIZARD_STORAGE_KEY, JSON.stringify(wizardState));
    }
  }, [wizardState]);

  const startWizard = useCallback((titleOnly: boolean = false) => {
    setWizardState({
      isActive: true,
      currentStep: 0,
      completedSteps: 0,
      stepData: {},
      titleOnly,
    });
  }, []);

  const cancelWizard = useCallback(() => {
    setWizardState({
      isActive: false,
      currentStep: 0,
      completedSteps: 0,
      stepData: {},
      titleOnly: false,
    });
    
    // Clear localStorage
    if (typeof window !== 'undefined') {
      localStorage.removeItem(IMPORT_WIZARD_STORAGE_KEY);
    }
  }, []);

  const nextStep = useCallback(() => {
    if (wizardState.currentStep < TOTAL_STEPS - 1) {
      const nextStepIndex = wizardState.currentStep + 1;
      
      setWizardState(prev => ({
        ...prev,
        currentStep: nextStepIndex,
        completedSteps: Math.max(prev.completedSteps, nextStepIndex),
      }));
    }
  }, [wizardState.currentStep]);

  const previousStep = useCallback(() => {
    if (wizardState.currentStep > 0) {
      const prevStepIndex = wizardState.currentStep - 1;
      
      setWizardState(prev => ({
        ...prev,
        currentStep: prevStepIndex,
      }));
    }
  }, [wizardState.currentStep]);

  const completeWizard = useCallback(() => {
    setWizardState({
      isActive: false,
      currentStep: 0,
      completedSteps: 0,
      stepData: {},
      titleOnly: false,
    });
    
    // Clear localStorage
    if (typeof window !== 'undefined') {
      localStorage.removeItem(IMPORT_WIZARD_STORAGE_KEY);
    }
    
    // Invalidate library query to refresh the library
    void queryClient.invalidateQueries({ queryKey: ['series', 'library'] });}, [queryClient]);

  const setStepData = useCallback((stepIndex: number, data: unknown) => {
    setWizardState(prev => ({
      ...prev,
      stepData: {
        ...prev.stepData,
        [stepIndex]: data,
      },
    }));
  }, []);

  const getStepData = useCallback((stepIndex: number): unknown => {
    return wizardState.stepData[stepIndex];
  }, [wizardState.stepData]);

  const contextValue: ImportWizardContextType = useMemo(() => ({
    isWizardActive: wizardState.isActive,
    currentStep: wizardState.currentStep,
    totalSteps: TOTAL_STEPS,
    isLoading: false, // Import wizard doesn't need complex loading states like setup wizard
    titleOnly: wizardState.titleOnly,
    nextStep,
    previousStep,
    completeWizard,
    startWizard,
    cancelWizard,
    setStepData,
    getStepData,
  }), [wizardState, nextStep, previousStep, completeWizard, startWizard, cancelWizard, setStepData, getStepData]);

  return (
    <ImportWizardContext.Provider value={contextValue}>
      {children}
    </ImportWizardContext.Provider>
  );
}

export function useImportWizard() {
  const context = useContext(ImportWizardContext);
  if (context === undefined) {
    throw new Error('useImportWizard must be used within an ImportWizardProvider');
  }
  return context;
}
