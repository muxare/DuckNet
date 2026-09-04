namespace DuckNet.Kernel.Persistence;

public static class CenterSchema
{
    public const string DeadLetterQueue = """
        CREATE TABLE IF NOT EXISTS dead_letter_queue (
          id INTEGER PRIMARY KEY AUTOINCREMENT,
          consumer_group TEXT NOT NULL,
          event_id TEXT NOT NULL,
          payload_json TEXT NOT NULL,
          error TEXT NOT NULL,
          failed_at TEXT NOT NULL,
          attempts INTEGER NOT NULL
        );

        CREATE UNIQUE INDEX IF NOT EXISTS dlq_group_event
          ON dead_letter_queue (consumer_group, event_id);
        """;

    public const string Telemetry = """
        CREATE TABLE IF NOT EXISTS event_log (
          offset INTEGER PRIMARY KEY AUTOINCREMENT,
          event_id TEXT NOT NULL UNIQUE,
          partition_key TEXT NOT NULL,
          type TEXT NOT NULL,
          version INTEGER NOT NULL,
          sequence_number INTEGER NOT NULL,
          payload_json TEXT NOT NULL,
          occurred_at TEXT NOT NULL,
          trace_id TEXT,
          causation_id TEXT
        );

        CREATE TABLE IF NOT EXISTS outbox (
          id INTEGER PRIMARY KEY AUTOINCREMENT,
          event_id TEXT NOT NULL,
          payload_json TEXT NOT NULL,
          published_at TEXT NULL
        );

        CREATE INDEX IF NOT EXISTS outbox_unpublished
          ON outbox (id) WHERE published_at IS NULL;

        CREATE TABLE IF NOT EXISTS consumer_offsets (
          consumer_group TEXT PRIMARY KEY,
          last_offset INTEGER NOT NULL
        );

        CREATE TABLE IF NOT EXISTS inbox (
          consumer_group TEXT NOT NULL,
          event_id TEXT NOT NULL,
          processed_at TEXT NOT NULL,
          PRIMARY KEY (consumer_group, event_id)
        );

        CREATE TABLE IF NOT EXISTS duck_state (
          duck_id TEXT PRIMARY KEY,
          last_seq INTEGER NOT NULL
        );

        CREATE TABLE IF NOT EXISTS squeak_counts (
          consumer_group TEXT NOT NULL,
          duck_id TEXT NOT NULL,
          count INTEGER NOT NULL,
          last_seq INTEGER NOT NULL,
          PRIMARY KEY (consumer_group, duck_id)
        );
        """ + DeadLetterQueue;

    public const string Alarm = """
        CREATE TABLE IF NOT EXISTS outbox (
          id INTEGER PRIMARY KEY AUTOINCREMENT,
          event_id TEXT NOT NULL,
          payload_json TEXT NOT NULL,
          published_at TEXT NULL
        );

        CREATE INDEX IF NOT EXISTS outbox_unpublished
          ON outbox (id) WHERE published_at IS NULL;

        CREATE TABLE IF NOT EXISTS consumer_offsets (
          consumer_group TEXT PRIMARY KEY,
          last_offset INTEGER NOT NULL
        );

        CREATE TABLE IF NOT EXISTS inbox (
          consumer_group TEXT NOT NULL,
          event_id TEXT NOT NULL,
          processed_at TEXT NOT NULL,
          PRIMARY KEY (consumer_group, event_id)
        );

        CREATE TABLE IF NOT EXISTS squeak_window (
          duck_id TEXT NOT NULL,
          event_id TEXT NOT NULL,
          occurred_at TEXT NOT NULL,
          PRIMARY KEY (duck_id, event_id)
        );

        CREATE INDEX IF NOT EXISTS squeak_window_duck_at
          ON squeak_window (duck_id, occurred_at);

        CREATE TABLE IF NOT EXISTS duck_progress (
          duck_id TEXT PRIMARY KEY,
          last_seq INTEGER NOT NULL
        );

        CREATE TABLE IF NOT EXISTS duck_alarm_state (
          duck_id TEXT PRIMARY KEY,
          active INTEGER NOT NULL,
          last_seq INTEGER NOT NULL,
          last_alarm_event_id TEXT
        );

        CREATE TABLE IF NOT EXISTS alarms (
          id INTEGER PRIMARY KEY AUTOINCREMENT,
          duck_id TEXT NOT NULL,
          rate REAL NOT NULL,
          window_start TEXT NOT NULL,
          raised_at TEXT NOT NULL,
          event_id TEXT NOT NULL UNIQUE
        );

        CREATE TABLE IF NOT EXISTS asset_health (
          asset_id TEXT PRIMARY KEY,
          score REAL NOT NULL,
          predicted_failure_at TEXT NOT NULL,
          recommended_service TEXT NOT NULL,
          vibration_mm_s REAL NOT NULL,
          temperature_c REAL NOT NULL,
          engine_hours REAL NOT NULL,
          scored_at TEXT NOT NULL
        );
        """ + DeadLetterQueue;

    public const string Dashboard = """
        CREATE TABLE IF NOT EXISTS consumer_offsets (
          consumer_group TEXT PRIMARY KEY,
          last_offset INTEGER NOT NULL
        );

        CREATE TABLE IF NOT EXISTS inbox (
          consumer_group TEXT NOT NULL,
          event_id TEXT NOT NULL,
          processed_at TEXT NOT NULL,
          PRIMARY KEY (consumer_group, event_id)
        );

        CREATE TABLE IF NOT EXISTS squeaks_by_duck_hour (
          duck_id TEXT NOT NULL,
          hour_utc TEXT NOT NULL,
          count INTEGER NOT NULL,
          volume_db REAL,
          PRIMARY KEY (duck_id, hour_utc)
        );

        CREATE TABLE IF NOT EXISTS asset_health_latest (
          asset_id TEXT PRIMARY KEY,
          score REAL NOT NULL,
          predicted_failure_at TEXT NOT NULL,
          recommended_service TEXT NOT NULL,
          vibration_mm_s REAL NOT NULL,
          temperature_c REAL NOT NULL,
          engine_hours REAL NOT NULL,
          scored_at TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS service_cases_by_asset (
          alarm_id TEXT PRIMARY KEY,
          asset_id TEXT NOT NULL,
          state TEXT NOT NULL,
          total_cents INTEGER NOT NULL,
          updated_at TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS confirmed_orders (
          order_id TEXT PRIMARY KEY,
          alarm_id TEXT NOT NULL,
          asset_id TEXT NOT NULL,
          total_cents INTEGER NOT NULL,
          confirmed_at TEXT NOT NULL
        );
        """ + DeadLetterQueue;

    public const string Billing = """
        CREATE TABLE IF NOT EXISTS outbox (
          id INTEGER PRIMARY KEY AUTOINCREMENT,
          event_id TEXT NOT NULL,
          payload_json TEXT NOT NULL,
          published_at TEXT NULL
        );

        CREATE INDEX IF NOT EXISTS outbox_unpublished
          ON outbox (id) WHERE published_at IS NULL;

        CREATE TABLE IF NOT EXISTS consumer_offsets (
          consumer_group TEXT PRIMARY KEY,
          last_offset INTEGER NOT NULL
        );

        CREATE TABLE IF NOT EXISTS inbox (
          consumer_group TEXT NOT NULL,
          event_id TEXT NOT NULL,
          processed_at TEXT NOT NULL,
          PRIMARY KEY (consumer_group, event_id)
        );

        CREATE TABLE IF NOT EXISTS duck_progress (
          duck_id TEXT PRIMARY KEY,
          last_seq INTEGER NOT NULL
        );

        CREATE TABLE IF NOT EXISTS billing_sagas (
          alarm_id TEXT PRIMARY KEY,
          duck_id TEXT NOT NULL,
          state TEXT NOT NULL,
          amount_cents INTEGER NOT NULL,
          reserved_at TEXT NOT NULL,
          expires_at TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS parts (
          sku TEXT PRIMARY KEY,
          name TEXT NOT NULL,
          unit_cents INTEGER NOT NULL
        );

        CREATE TABLE IF NOT EXISTS equipment_assets (
          asset_id TEXT PRIMARY KEY,
          equipment_model TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS fitment (
          equipment_model TEXT NOT NULL,
          sku TEXT NOT NULL,
          quantity INTEGER NOT NULL,
          PRIMARY KEY (equipment_model, sku)
        );

        CREATE TABLE IF NOT EXISTS inventory (
          sku TEXT PRIMARY KEY,
          qty_on_hand INTEGER NOT NULL,
          qty_reserved INTEGER NOT NULL
        );

        CREATE TABLE IF NOT EXISTS inventory_holds (
          alarm_id TEXT NOT NULL,
          sku TEXT NOT NULL,
          quantity INTEGER NOT NULL,
          PRIMARY KEY (alarm_id, sku)
        );

        CREATE TABLE IF NOT EXISTS service_case_lines (
          alarm_id TEXT NOT NULL,
          sku TEXT NOT NULL,
          quantity INTEGER NOT NULL,
          unit_cents INTEGER NOT NULL,
          PRIMARY KEY (alarm_id, sku)
        );
        """ + DeadLetterQueue;
}
