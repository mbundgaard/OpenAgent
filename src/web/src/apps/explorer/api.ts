import { apiFetch } from '../../auth/api';

export interface FileEntry {
  name: string;
  path: string;
  isDirectory: boolean;
  size: number | null;
  modifiedAt: string;
}

export async function listDirectory(path: string = ''): Promise<FileEntry[]> {
  const params = path ? `?path=${encodeURIComponent(path)}` : '';
  const res = await apiFetch(`/api/files${params}`);
  if (!res.ok) throw new Error(`Failed to list directory: ${res.status}`);
  return res.json();
}
