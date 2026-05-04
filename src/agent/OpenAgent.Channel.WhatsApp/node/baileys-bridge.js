// Baileys bridge — spawned by WhatsAppNodeProcess (.NET).
// Communication: stdin/stdout JSON lines. All logs go to stderr.

import { createInterface } from "node:readline";
import { parseArgs } from "node:util";
import {
  DisconnectReason,
  fetchLatestBaileysVersion,
  makeCacheableSignalKeyStore,
  makeWASocket,
  useMultiFileAuthState,
} from "@whiskeysockets/baileys";
import pino from "pino";

// --- Parse CLI args ---

const { values: args } = parseArgs({
  options: {
    "auth-dir": { type: "string" },
  },
  strict: true,
});

const authDir = args["auth-dir"];
if (!authDir) {
  console.error("FATAL: --auth-dir is required");
  process.exit(1);
}

// --- Helpers ---

/** Write a JSON line to stdout (the only way to talk to .NET). */
function emit(obj) {
  process.stdout.write(JSON.stringify(obj) + "\n");
}

/** Map a DisconnectReason status code to a human-readable name. */
function disconnectReasonName(statusCode) {
  for (const [name, value] of Object.entries(DisconnectReason)) {
    if (value === statusCode) {
      return name;
    }
  }
  return String(statusCode);
}

/** Extract the status code from a Baileys disconnect error. */
function getStatusCode(error) {
  // Baileys wraps the status in error.output.statusCode (Boom) or error.statusCode
  return (
    error?.output?.statusCode ??
    error?.statusCode ??
    undefined
  );
}

/**
 * Extract text from a Baileys message object.
 * Returns undefined if the message has no text content.
 */
function extractText(message) {
  if (!message) {
    return undefined;
  }
  // Plain conversation text
  if (typeof message.conversation === "string" && message.conversation.trim()) {
    return message.conversation.trim();
  }
  // Extended text (replies, links, etc.)
  const extended = message.extendedTextMessage?.text;
  if (extended?.trim()) {
    return extended.trim();
  }
  return undefined;
}

/**
 * Extract the replied-to message ID, if this message is a reply.
 * Baileys exposes it at message.extendedTextMessage.contextInfo.stanzaId.
 * Returns undefined for non-replies and plain conversation messages.
 */
function extractReplyTo(message) {
  if (!message) {
    return undefined;
  }
  const stanzaId = message.extendedTextMessage?.contextInfo?.stanzaId;
  if (typeof stanzaId === "string" && stanzaId.length > 0) {
    return stanzaId;
  }
  return undefined;
}

/**
 * Extract the sender's canonical identifier from a message key.
 * For group messages the sender is key.participant; for DMs it is key.remoteJid.
 *
 * Returns the final canonical form (no further formatting on the .NET side):
 *   - "+E164PHONE" for @s.whatsapp.net JIDs (real phone numbers, leading +)
 *   - "+E164PHONE" for @lid JIDs that Baileys can resolve via signalRepository.lidMapping
 *   - "lid:NUMBER" for @lid JIDs that cannot be resolved (privacy-mode, no PN ever shared)
 *   - the raw JID otherwise (groups, broadcasts — caller decides what to do)
 *
 * Async because lid->phone resolution hits the SignalKeyStore. Pass the live `sock` so we
 * can use sock.signalRepository.lidMapping.getPNForLID. If sock is null/undefined we skip
 * resolution and fall back to "lid:NUMBER".
 */
async function extractSender(sock, key) {
  const jid = key.participant || key.remoteJid;
  if (!jid) {
    return undefined;
  }
  // Strip ":device" suffix — same person on phone (`:0`) vs Web/Desktop (`:N`)
  // should produce one canonical sender id.
  const stripDevice = (user) => user.split(":")[0];

  if (jid.endsWith("@s.whatsapp.net")) {
    return "+" + stripDevice(jid.slice(0, -"@s.whatsapp.net".length));
  }
  if (jid.endsWith("@lid")) {
    // Try to resolve LID -> phone JID via Baileys' signalRepository (works when
    // Baileys has previously seen a LID/PN pair for this user, e.g. via a prior DM
    // or shared metadata).
    try {
      const lidMapping = sock?.signalRepository?.lidMapping;
      if (lidMapping?.getPNForLID) {
        const pnJid = await lidMapping.getPNForLID(jid);
        if (pnJid && pnJid.endsWith("@s.whatsapp.net")) {
          return "+" + stripDevice(pnJid.slice(0, -"@s.whatsapp.net".length));
        }
      }
    } catch (err) {
      console.error("LID->PN resolution failed for", jid, err);
    }
    return "lid:" + stripDevice(jid.slice(0, -"@lid".length));
  }
  return jid;
}

// --- Uncaught error handler ---

process.on("uncaughtException", (err) => {
  console.error("FATAL uncaught exception:", err);
  process.exit(1);
});
process.on("unhandledRejection", (reason) => {
  console.error("FATAL unhandled rejection:", reason);
  process.exit(1);
});

// --- Create Baileys socket ---

async function main() {
  console.error(`Starting Baileys bridge, authDir=${authDir}`);

  // Silent logger — Baileys is very noisy, route nothing to stdout
  const logger = pino({ level: "silent" });

  const { state, saveCreds } = await useMultiFileAuthState(authDir);
  const { version } = await fetchLatestBaileysVersion();

  const sock = makeWASocket({
    auth: {
      creds: state.creds,
      keys: makeCacheableSignalKeyStore(state.keys, logger),
    },
    version,
    logger,
    printQRInTerminal: false,
    browser: ["OpenAgent", "Server", "1.0.0"],
    syncFullHistory: false,
    markOnlineOnConnect: false,
  });

  // Persist credential updates
  sock.ev.on("creds.update", async () => {
    try {
      await saveCreds();
    } catch (err) {
      console.error("Failed to save creds:", err);
    }
  });

  // --- Connection events ---

  sock.ev.on("connection.update", (update) => {
    try {
      const { connection, lastDisconnect, qr } = update;

      // QR code for pairing
      if (qr) {
        console.error("QR code received, emitting to .NET");
        emit({ type: "qr", data: qr });
      }

      // Connected
      if (connection === "open") {
        const selfJid = sock.user?.id || "unknown";
        console.error(`Connected as ${selfJid}`);
        emit({ type: "connected", jid: selfJid });
      }

      // Disconnected — emit reason and exit
      if (connection === "close") {
        const statusCode = getStatusCode(lastDisconnect?.error);
        const isLoggedOut = statusCode === DisconnectReason.loggedOut;
        const reason = isLoggedOut ? "loggedOut" : disconnectReasonName(statusCode);
        console.error(`Disconnected: reason=${reason}, statusCode=${statusCode}`);
        emit({ type: "disconnected", reason });
        // No reconnect — .NET side handles restart
        process.exit(isLoggedOut ? 2 : 1);
      }
    } catch (err) {
      console.error("Error in connection.update handler:", err);
    }
  });

  // --- Inbound messages ---

  sock.ev.on("messages.upsert", async (upsert) => {
    try {
      if (upsert.type !== "notify") {
        return;
      }

      for (const msg of upsert.messages) {
        const key = msg.key;
        const chatId = key?.remoteJid;

        // Skip status broadcasts
        if (chatId === "status@broadcast") {
          continue;
        }

        // Skip messages from self
        if (key?.fromMe === true) {
          continue;
        }

        // Extract text — skip non-text messages
        const text = extractText(msg.message);
        if (!text) {
          continue;
        }

        const from = await extractSender(sock, key);
        const timestamp = msg.messageTimestamp
          ? Number(msg.messageTimestamp)
          : Math.floor(Date.now() / 1000);

        emit({
          type: "message",
          id: key.id || undefined,
          chatId,
          from,
          pushName: msg.pushName || undefined,
          text,
          replyTo: extractReplyTo(msg.message),
          timestamp,
        });
      }
    } catch (err) {
      console.error("Error in messages.upsert handler:", err);
    }
  });

  // --- Handle WebSocket-level errors ---

  if (sock.ws && typeof sock.ws.on === "function") {
    sock.ws.on("error", (err) => {
      console.error("WebSocket error:", err);
    });
  }

  // --- stdin protocol (commands from .NET) ---

  const rl = createInterface({ input: process.stdin });

  rl.on("line", async (line) => {
    try {
      const cmd = JSON.parse(line);

      switch (cmd.type) {
        case "send":
          try {
            const result = await sock.sendMessage(cmd.chatId, { text: cmd.text });
            const id = result?.key?.id;
            if (typeof id === "string" && id.length > 0) {
              emit({ type: "sent", correlationId: cmd.correlationId, id });
            } else {
              emit({ type: "sent", correlationId: cmd.correlationId, message: "send returned no message id" });
            }
          } catch (sendErr) {
            console.error("send failed:", sendErr);
            emit({ type: "sent", correlationId: cmd.correlationId, message: String(sendErr?.message || sendErr) });
          }
          break;

        case "composing":
          await sock.sendPresenceUpdate("composing", cmd.chatId);
          break;

        case "ping":
          emit({ type: "pong" });
          break;

        case "shutdown":
          console.error("Shutdown requested");
          try {
            sock.end(undefined);
          } catch {
            // ignore close errors
          }
          process.exit(0);
          break;

        default:
          console.error(`Unknown command type: ${cmd.type}`);
          break;
      }
    } catch (err) {
      console.error("Error processing stdin command:", err);
    }
  });

  // If stdin closes (parent process died), shut down
  rl.on("close", () => {
    console.error("stdin closed, shutting down");
    try {
      sock.end(undefined);
    } catch {
      // ignore
    }
    process.exit(0);
  });

  console.error("Baileys bridge initialized, waiting for connection...");
}

main().catch((err) => {
  console.error("FATAL error in main():", err);
  process.exit(1);
});
