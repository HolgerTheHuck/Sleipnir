import type { SleipnirRequest, SleipnirResponse } from 'sleipnir-client';

export interface HistoryEntry {
  id: string;
  timestamp: number;
  request: SleipnirRequest;
  response: SleipnirResponse | null;
  duration: string;
  error?: string;
}

const STORAGE_KEY = 'sleipnir-history';
const MAX_ENTRIES = 100;

function loadFromStorage(): HistoryEntry[] {
  try {
    const raw = localStorage.getItem(STORAGE_KEY);
    if (!raw) return [];
    return JSON.parse(raw) as HistoryEntry[];
  } catch {
    return [];
  }
}

function saveToStorage(entries: HistoryEntry[]) {
  try {
    localStorage.setItem(STORAGE_KEY, JSON.stringify(entries.slice(0, MAX_ENTRIES)));
  } catch {
    /* storage full or unavailable */
  }
}

class HistoryState {
  entries = $state<HistoryEntry[]>(loadFromStorage());
  isOpen = $state(false);

  addEntry(entry: HistoryEntry) {
    this.entries.unshift(entry);
    if (this.entries.length > MAX_ENTRIES) {
      this.entries = this.entries.slice(0, MAX_ENTRIES);
    }
    saveToStorage(this.entries);
  }

  removeEntry(id: string) {
    this.entries = this.entries.filter((e) => e.id !== id);
    saveToStorage(this.entries);
  }

  clearHistory() {
    this.entries = [];
    saveToStorage(this.entries);
  }

  /** Überschreibt die History aus einem Workspace-Import (deckelt auf MAX_ENTRIES). */
  restoreFromWorkspace(entries: HistoryEntry[]): void {
    if (!Array.isArray(entries)) return;
    this.entries = entries.slice(0, MAX_ENTRIES);
    saveToStorage(this.entries);
  }

  toggle() {
    this.isOpen = !this.isOpen;
  }
}

export const historyState = new HistoryState();
