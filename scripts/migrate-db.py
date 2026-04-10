"""
Migrate OpenAgent SQLite database from old format to new format.

Changes:
  1. Schema: adds missing columns via ALTER TABLE (same as TryAddColumn in C#)
  2. Conversation IDs: old composite IDs like "telegram:connId:chatId" are replaced
     with GUIDs, and the channel info is split into ChannelType/ConnectionId/ChannelChatId
  3. Messages: ConversationId updated to match the new conversation GUID
  4. Modality column added to Messages (default 0 = Text)

Usage:
  python scripts/migrate-db.py <path-to-openagent.db>

Creates a backup at <path>.bak before modifying.
"""

import sqlite3
import sys
import shutil
import os
import uuid
import re


def try_add_column(conn, table, column, col_type):
    """Add a column if it doesn't exist (mirrors TryAddColumn in C#)."""
    try:
        conn.execute(f"ALTER TABLE {table} ADD COLUMN {column} {col_type}")
        print(f"  Added {table}.{column} ({col_type})")
    except sqlite3.OperationalError:
        pass  # Column already exists


def migrate_schema(conn):
    """Add all columns that the current code expects."""
    print("Schema migration:")

    # Messages columns
    try_add_column(conn, "Messages", "ToolCalls", "TEXT")
    try_add_column(conn, "Messages", "ToolCallId", "TEXT")
    try_add_column(conn, "Messages", "ChannelMessageId", "TEXT")
    try_add_column(conn, "Messages", "ReplyToChannelMessageId", "TEXT")
    try_add_column(conn, "Messages", "PromptTokens", "INTEGER")
    try_add_column(conn, "Messages", "CompletionTokens", "INTEGER")
    try_add_column(conn, "Messages", "ElapsedMs", "INTEGER")
    try_add_column(conn, "Messages", "Modality", "INTEGER NOT NULL DEFAULT 0")

    # Conversations columns
    try_add_column(conn, "Conversations", "LastPromptTokens", "INTEGER")
    try_add_column(conn, "Conversations", "Context", "TEXT")
    try_add_column(conn, "Conversations", "CompactedUpToRowId", "INTEGER")
    try_add_column(conn, "Conversations", "CompactionRunning", "INTEGER NOT NULL DEFAULT 0")
    try_add_column(conn, "Conversations", "Provider", "TEXT NOT NULL DEFAULT ''")
    try_add_column(conn, "Conversations", "Model", "TEXT NOT NULL DEFAULT ''")
    try_add_column(conn, "Conversations", "TotalPromptTokens", "INTEGER NOT NULL DEFAULT 0")
    try_add_column(conn, "Conversations", "TotalCompletionTokens", "INTEGER NOT NULL DEFAULT 0")
    try_add_column(conn, "Conversations", "TurnCount", "INTEGER NOT NULL DEFAULT 0")
    try_add_column(conn, "Conversations", "LastActivity", "TEXT")
    try_add_column(conn, "Conversations", "ActiveSkills", "TEXT")
    try_add_column(conn, "Conversations", "ChannelType", "TEXT")
    try_add_column(conn, "Conversations", "ConnectionId", "TEXT")
    try_add_column(conn, "Conversations", "ChannelChatId", "TEXT")

    conn.commit()


def migrate_conversation_ids(conn):
    """Replace composite IDs (e.g. telegram:connId:chatId) with GUIDs."""
    print("\nConversation ID migration:")

    cursor = conn.execute("SELECT Id FROM Conversations")
    rows = cursor.fetchall()

    # Pattern: channelType:connectionId:chatId
    composite_pattern = re.compile(r"^(\w+):([^:]+):(.+)$")
    migrated = 0

    for (old_id,) in rows:
        match = composite_pattern.match(old_id)
        if not match:
            print(f"  {old_id[:40]}... — already a GUID or non-composite, skipping")
            continue

        channel_type, connection_id, chat_id = match.groups()
        new_id = str(uuid.uuid4())

        print(f"  {old_id} -> {new_id}")
        print(f"    channel={channel_type} connection={connection_id} chat={chat_id}")

        # Update conversation: new ID + populate channel columns
        conn.execute(
            """UPDATE Conversations
               SET Id = ?, ChannelType = ?, ConnectionId = ?, ChannelChatId = ?
               WHERE Id = ?""",
            (new_id, channel_type, connection_id, chat_id, old_id),
        )

        # Update all messages to reference the new conversation ID
        msg_count = conn.execute(
            "UPDATE Messages SET ConversationId = ? WHERE ConversationId = ?",
            (new_id, old_id),
        ).rowcount
        print(f"    Updated {msg_count} messages")

        migrated += 1

    conn.commit()
    print(f"  Migrated {migrated} conversation(s)")


def print_summary(conn):
    """Print a summary of the database after migration."""
    print("\nPost-migration summary:")
    conv_count = conn.execute("SELECT COUNT(*) FROM Conversations").fetchone()[0]
    msg_count = conn.execute("SELECT COUNT(*) FROM Messages").fetchone()[0]
    print(f"  {conv_count} conversations, {msg_count} messages")

    cursor = conn.execute(
        "SELECT Id, Source, ChannelType, ConnectionId, ChannelChatId FROM Conversations"
    )
    for row in cursor:
        cid, source, ch_type, conn_id, chat_id = row
        binding = f"{ch_type}:{conn_id}:{chat_id}" if ch_type else "none"
        print(f"  {cid[:12]}... source={source} channel={binding}")


def main():
    if len(sys.argv) != 2:
        print(f"Usage: python {sys.argv[0]} <path-to-openagent.db>")
        sys.exit(1)

    db_path = sys.argv[1]

    # Create backup (include WAL/SHM files if present)
    backup_path = db_path + ".bak"
    shutil.copy2(db_path, backup_path)
    for ext in ("-wal", "-shm"):
        wal_path = db_path + ext
        if os.path.exists(wal_path):
            shutil.copy2(wal_path, backup_path + ext)
    print(f"Backup created at {backup_path}")

    conn = sqlite3.connect(db_path)
    conn.execute("PRAGMA journal_mode = WAL")

    # Flush any pending WAL writes into the main db file before migrating
    conn.execute("PRAGMA wal_checkpoint(TRUNCATE)")
    print("WAL checkpoint complete\n")

    conn.execute("PRAGMA foreign_keys = OFF")  # Disable FK checks during migration

    try:
        migrate_schema(conn)
        migrate_conversation_ids(conn)
        print_summary(conn)
    finally:
        conn.execute("PRAGMA foreign_keys = ON")
        conn.close()

    print("\nMigration complete.")


if __name__ == "__main__":
    main()
