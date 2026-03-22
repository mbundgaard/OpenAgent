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
