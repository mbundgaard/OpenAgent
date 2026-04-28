import { apiFetch } from '../../auth/api';

export interface ConfigField {
  key: string;
  label: string;
  type: string; // "String" | "Secret" | "Enum" | "Text"
  required: boolean;
  default_value?: string;
  options?: string[];
}

export interface ProviderInfo {
  key: string;
  capabilities: string[]; // e.g. ["text"], ["voice"], []
}

/** List all configurable providers with their capability tags. */
export async function listProviders(): Promise<ProviderInfo[]> {
  const res = await apiFetch('/api/admin/providers');
  return res.json();
}

/** Get the config schema (fields) for a provider. */
export async function getProviderConfig(key: string): Promise<ConfigField[]> {
  const res = await apiFetch(`/api/admin/providers/${key}/config`);
  return res.json();
}

/** Get the current saved values for a provider. */
export async function getProviderValues(key: string): Promise<Record<string, unknown>> {
  const res = await apiFetch(`/api/admin/providers/${key}/values`);
  return res.json();
}

/** Get available models for a provider. */
export async function getProviderModels(key: string): Promise<string[]> {
  const res = await apiFetch(`/api/admin/providers/${key}/models`);
  return res.json();
}

/** Resolve a just-in-time auth URL for a provider field. */
export async function getProviderAuthLink(key: string, fieldKey: string): Promise<string> {
  const res = await apiFetch(`/api/admin/providers/${key}/auth-link/${fieldKey}`);
  const body = await res.json();
  return String(body.url ?? '');
}

/** Save provider configuration. */
export async function saveProviderConfig(key: string, config: Record<string, string>): Promise<void> {
  await apiFetch(`/api/admin/providers/${key}/config`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(config),
  });
}

/** Get all system prompt file contents. */
export async function getSystemPrompt(): Promise<Record<string, string | null>> {
  const res = await apiFetch('/api/admin/system-prompt');
  return res.json();
}

/** Save system prompt files (partial update). */
export async function saveSystemPrompt(data: Record<string, string>): Promise<void> {
  await apiFetch('/api/admin/system-prompt', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(data),
  });
}

/** Reload system prompt files and skills from disk. */
export async function reloadSystemPrompt(): Promise<void> {
  await apiFetch('/api/admin/system-prompt/reload', { method: 'POST' });
}

// --- Connections ---

export interface ConnectionInfo {
  id: string;
  name: string;
  type: string;
  enabled: boolean;
  allowNewConversations: boolean;
  conversationId: string;
  config: Record<string, unknown>;
  status: string;
}

export async function listConnections(): Promise<ConnectionInfo[]> {
  const res = await apiFetch('/api/connections');
  return res.json();
}

export async function createConnection(data: {
  name: string;
  type: string;
  enabled: boolean;
  config: Record<string, unknown>;
}): Promise<ConnectionInfo> {
  const res = await apiFetch('/api/connections', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(data),
  });
  return res.json();
}

export async function updateConnection(id: string, data: Record<string, unknown>): Promise<ConnectionInfo> {
  const res = await apiFetch(`/api/connections/${id}`, {
    method: 'PUT',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(data),
  });
  return res.json();
}

export async function deleteConnection(id: string): Promise<void> {
  await apiFetch(`/api/connections/${id}`, { method: 'DELETE' });
}

export async function startConnection(id: string): Promise<void> {
  await apiFetch(`/api/connections/${id}/start`, { method: 'POST' });
}

export async function stopConnection(id: string): Promise<void> {
  await apiFetch(`/api/connections/${id}/stop`, { method: 'POST' });
}

// --- Connection Types ---

export interface ChannelSetupStep {
  type: string;       // "qr-code", "none"
  endpoint: string | null;
}

export interface ChannelTypeInfo {
  type: string;
  displayName: string;
  configFields: ConfigField[];
  setupStep: ChannelSetupStep | null;
}

export interface WhatsAppQrResponse {
  status: string;
  qr: string | null;
  error: string | null;
}

export async function getConnectionTypes(): Promise<ChannelTypeInfo[]> {
  const res = await apiFetch('/api/connections/types');
  return res.json();
}

export async function getWhatsAppQr(connectionId: string): Promise<WhatsAppQrResponse> {
  const res = await apiFetch(`/api/connections/${connectionId}/whatsapp/qr`);
  return res.json();
}
