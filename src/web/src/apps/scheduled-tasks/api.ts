import { apiFetch } from '../../auth/api';

export interface ScheduleConfig {
  cron?: string | null;
  timezone?: string | null;
  intervalMs?: number | null;
  at?: string | null;
}

export type TaskRunStatus = 'Success' | 'Error';

export interface ScheduledTaskState {
  nextRunAt?: string | null;
  lastRunAt?: string | null;
  lastStatus?: TaskRunStatus | null;
  lastError?: string | null;
  consecutiveErrors: number;
}

export interface ScheduledTask {
  id: string;
  name: string;
  description?: string | null;
  enabled: boolean;
  deleteAfterRun: boolean;
  schedule: ScheduleConfig;
  prompt: string;
  conversationId?: string | null;
  state: ScheduledTaskState;
}

export interface CreateTaskRequest {
  id: string;
  name: string;
  description?: string | null;
  enabled?: boolean;
  deleteAfterRun?: boolean;
  schedule: ScheduleConfig;
  prompt: string;
  conversationId?: string | null;
}

export interface UpdateTaskRequest {
  name?: string;
  description?: string | null;
  enabled?: boolean;
  prompt?: string;
  schedule?: ScheduleConfig;
  conversationId?: string | null;
}

export async function listTasks(): Promise<ScheduledTask[]> {
  const res = await apiFetch('/api/scheduled-tasks');
  return res.json();
}

export async function getTask(taskId: string): Promise<ScheduledTask> {
  const res = await apiFetch(`/api/scheduled-tasks/${taskId}`);
  return res.json();
}

export async function createTask(task: CreateTaskRequest): Promise<ScheduledTask> {
  const res = await apiFetch('/api/scheduled-tasks', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(task),
  });
  if (!res.ok) {
    const body = await res.json().catch(() => ({ error: res.statusText }));
    throw new Error(body.error ?? `Failed to create task: ${res.status}`);
  }
  return res.json();
}

export async function updateTask(taskId: string, patch: UpdateTaskRequest): Promise<ScheduledTask> {
  const res = await apiFetch(`/api/scheduled-tasks/${taskId}`, {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(patch),
  });
  if (!res.ok) {
    const body = await res.json().catch(() => ({ error: res.statusText }));
    throw new Error(body.error ?? `Failed to update task: ${res.status}`);
  }
  return res.json();
}

export async function deleteTask(taskId: string): Promise<void> {
  const res = await apiFetch(`/api/scheduled-tasks/${taskId}`, { method: 'DELETE' });
  if (!res.ok && res.status !== 404) {
    throw new Error(`Failed to delete task: ${res.status}`);
  }
}

export async function runTaskNow(taskId: string): Promise<void> {
  const res = await apiFetch(`/api/scheduled-tasks/${taskId}/run`, { method: 'POST' });
  if (!res.ok) {
    throw new Error(`Failed to run task: ${res.status}`);
  }
}
