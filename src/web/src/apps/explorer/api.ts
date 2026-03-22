import { apiFetch } from '../../auth/api';

export interface FileEntry {
  name: string;
  path: string;
  isDirectory: boolean;
  size: number | null;
  modifiedAt: string;
}

export interface FileContent {
  path: string;
  name: string;
  content: string;
}

export async function listDirectory(path: string = ''): Promise<FileEntry[]> {
  const params = path ? `?path=${encodeURIComponent(path)}` : '';
  const res = await apiFetch(`/api/files${params}`);
  if (!res.ok) throw new Error(`Failed to list directory: ${res.status}`);
  return res.json();
}

export async function readFile(path: string): Promise<FileContent> {
  const res = await apiFetch(`/api/files/content?path=${encodeURIComponent(path)}`);
  if (!res.ok) throw new Error(`Failed to read file: ${res.status}`);
  return res.json();
}

export async function downloadFile(path: string): Promise<void> {
  const res = await apiFetch(`/api/files/download?path=${encodeURIComponent(path)}`);
  if (!res.ok) throw new Error(`Failed to download file: ${res.status}`);
  const blob = await res.blob();
  const url = URL.createObjectURL(blob);
  const a = document.createElement('a');
  a.href = url;
  a.download = path.split('/').pop() ?? 'download';
  document.body.appendChild(a);
  a.click();
  document.body.removeChild(a);
  URL.revokeObjectURL(url);
}

export async function renameFile(path: string, newName: string): Promise<FileEntry> {
  const res = await apiFetch('/api/files/rename', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ path, newName }),
  });
  if (!res.ok) {
    const err = await res.json().catch(() => ({ error: `Status ${res.status}` }));
    throw new Error(err.error ?? `Failed to rename: ${res.status}`);
  }
  return res.json();
}

export async function deleteFile(path: string): Promise<void> {
  const res = await apiFetch(`/api/files?path=${encodeURIComponent(path)}`, {
    method: 'DELETE',
  });
  if (!res.ok) throw new Error(`Failed to delete: ${res.status}`);
}
