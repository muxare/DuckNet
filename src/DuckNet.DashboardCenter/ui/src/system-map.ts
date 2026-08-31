export type CenterId = "telemetry" | "alarm" | "dashboard" | "billing" | "bus";

export type GraphKind = "event" | "http" | "internal";

export type MapEdge = {
  source: string;
  target: string;
  label?: string;
  kind?: GraphKind;
  term?: string;
  sourceHandle?: string;
  targetHandle?: string;
};

export type ProcessKind = "step" | "decision" | "store" | "drop";

export type ProcessNode = {
  id: string;
  label: string;
  kind: ProcessKind;
  note?: string;
  term?: string;
};

export type ObjectNode = {
  id: string;
  label: string;
  role?: string;
  term?: string;
};

export type CenterGraph<T> = {
  nodes: T[];
  edges: MapEdge[];
};

export type CodeLink = {
  path: string;
  why: string;
};

export type CenterMeta = {
  id: CenterId;
  title: string;
  role: string;
  owns: string;
  term: string;
  docs: string[];
  code: CodeLink[];
  process: CenterGraph<ProcessNode>;
  objects: CenterGraph<ObjectNode>;
  failure?: string;
};

export type OverviewNode = {
  id: CenterId;
  type: "center" | "port";
  position: { x: number; y: number };
};

export const overviewGraph: {
  nodes: OverviewNode[];
  edges: MapEdge[];
} = {
  nodes: [
    { id: "telemetry", type: "center", position: { x: 390, y: 8 } },
    { id: "bus", type: "port", position: { x: 378, y: 228 } },
    { id: "alarm", type: "center", position: { x: 28, y: 400 } },
    { id: "dashboard", type: "center", position: { x: 390, y: 400 } },
    { id: "billing", type: "center", position: { x: 752, y: 400 } },
  ],
  edges: [
    {
      source: "telemetry",
      target: "bus",
      label: "GET/POST /bus/events",
      kind: "http",
      term: "telemetry-http",
      sourceHandle: "bottom",
      targetHandle: "top",
    },
    {
      source: "bus",
      target: "alarm",
      label: "Squeaked",
      kind: "event",
      term: "squeaked",
      sourceHandle: "left",
      targetHandle: "top",
    },
    {
      source: "bus",
      target: "dashboard",
      label: "Squeaked",
      kind: "event",
      term: "squeaked",
      sourceHandle: "bottom",
      targetHandle: "top",
    },
    {
      source: "bus",
      target: "billing",
      label: "AlarmRaised / AlarmResolved",
      kind: "event",
      term: "alarm-raised",
      sourceHandle: "right",
      targetHandle: "top",
    },
    {
      source: "alarm",
      target: "telemetry",
      label: "AlarmRaised / AlarmResolved",
      kind: "http",
      term: "alarm-raised",
      sourceHandle: "right",
      targetHandle: "left",
    },
    {
      source: "billing",
      target: "telemetry",
      label: "FeeReserved / FeeReleased",
      kind: "http",
      term: "fee-reserved",
      sourceHandle: "left",
      targetHandle: "right",
    },
  ],
};

export const centers: CenterMeta[] = [
  {
    id: "telemetry",
    title: "TelemetryCenter",
    role: "Producer + log owner",
    owns: "event_log (system of record)",
    term: "telemetry",
    docs: ["docs/architecture/step-3.md", "docs/architecture/step-4.md"],
    code: [
      { path: "src/DuckNet.TelemetryCenter/TelemetryApp.cs", why: "composition root" },
      { path: "src/DuckNet.Kernel/Producer/DuckSimulator.cs", why: "LoudDuck emits Squeaked" },
      { path: "src/DuckNet.Kernel/Producer/TransactionalPublisher.cs", why: "state + outbox in one tx" },
      { path: "src/DuckNet.Kernel/Persistence/EventLogStore.cs", why: "append-only log" },
    ],
    process: {
      nodes: [
        { id: "sim", label: "DuckSimulator", kind: "step", term: "duck-simulator" },
        { id: "pub", label: "TransactionalPublisher", kind: "step", note: "duck_state + outbox, one tx", term: "transactional-publisher" },
        { id: "disp", label: "OutboxDispatcher", kind: "step", term: "outbox-dispatcher" },
        { id: "log", label: "event_log", kind: "store", term: "event-log" },
        { id: "http", label: "GET/POST /bus/events", kind: "step", term: "telemetry-http" },
      ],
      edges: [
        { source: "sim", target: "pub", kind: "internal" },
        { source: "pub", target: "disp", kind: "internal" },
        { source: "disp", target: "log", kind: "internal" },
        { source: "log", target: "http", kind: "http", label: "append / tail", term: "event-log" },
      ],
    },
    objects: {
      nodes: [
        { id: "sim", label: "DuckSimulator", role: "LoudDuck emits Squeaked", term: "duck-simulator" },
        { id: "pub", label: "TransactionalPublisher", role: "state + outbox, one tx", term: "transactional-publisher" },
        { id: "disp", label: "OutboxDispatcher", role: "drain outbox", term: "outbox-dispatcher" },
        { id: "log", label: "EventLogStore", role: "append-only log", term: "event-log" },
        { id: "http", label: "TelemetryApp HTTP", role: "GET/POST /bus/events", term: "telemetry-http" },
      ],
      edges: [
        { source: "sim", target: "pub", label: "emit(Squeaked)", kind: "internal", term: "squeaked" },
        { source: "pub", target: "disp", label: "outbox row", kind: "internal", term: "outbox" },
        { source: "disp", target: "log", label: "append", kind: "internal", term: "event-log" },
        { source: "log", target: "http", label: "tail / append", kind: "http", term: "telemetry-http" },
      ],
    },
  },
  {
    id: "alarm",
    title: "AlarmCenter",
    role: "Rate window → AlarmRaised / AlarmResolved",
    owns: "alarms + squeak_window",
    term: "alarm",
    docs: [
      "docs/architecture/step-4.md",
      "docs/architecture/step-7.md",
      "docs/architecture/step-8.md",
      "docs/architecture/step-10.md",
    ],
    code: [
      { path: "src/DuckNet.AlarmCenter/AlarmConsumer.cs", why: "handle.Squeaked" },
      { path: "src/DuckNet.AlarmCenter/AlarmStore.cs", why: "rate rule + outbox" },
      { path: "src/DuckNet.Kernel/Consumer/Inbox.cs", why: "dedupe EventId" },
      { path: "src/DuckNet.Kernel/Consumer/ShardWorkerPool.cs", why: "hot key isolation" },
    ],
    process: {
      nodes: [
        { id: "feed", label: "HttpLogTailFeeder", kind: "step", term: "http-log-tail-feeder" },
        { id: "dup", label: "Duplicator + Shuffler", kind: "step", note: "after log read", term: "duplicator-shuffler" },
        { id: "bus", label: "IEventBus", kind: "step", term: "bus" },
        { id: "up", label: "upcast Squeaked", kind: "step", term: "upcast" },
        { id: "shard", label: "ShardWorkerPool", kind: "step", term: "shard-worker-pool" },
        { id: "seq", label: "PerKeySequencer", kind: "step", term: "per-key-sequencer" },
        { id: "retry", label: "RetryPipeline", kind: "step", term: "retry-pipeline" },
        { id: "check", label: "Duplicate EventId?", kind: "decision", term: "event-id" },
        { id: "drop", label: "Drop", kind: "drop", term: "drop" },
        { id: "inbox", label: "inbox + AlarmStore", kind: "store", note: "one tx; own SQLite", term: "inbox" },
        { id: "rate", label: "Rate rule (ALARM_WINDOW)", kind: "step", term: "rate-rule" },
        { id: "out", label: "outbox AlarmRaised / AlarmResolved", kind: "step", term: "alarm-raised" },
        { id: "disp", label: "RemoteOutboxDispatcher", kind: "step", note: "POST /bus/events", term: "remote-outbox-dispatcher" },
        { id: "dlq", label: "DLQ", kind: "store", note: "retry exhaust; offset advances", term: "dlq" },
      ],
      edges: [
        { source: "feed", target: "dup", kind: "http" },
        { source: "dup", target: "bus", kind: "internal", label: "PublishAsync", term: "bus" },
        { source: "bus", target: "up", kind: "event", label: "Squeaked", term: "squeaked" },
        { source: "up", target: "shard", kind: "internal" },
        { source: "shard", target: "seq", kind: "internal" },
        { source: "seq", target: "retry", kind: "internal" },
        { source: "retry", target: "check", kind: "internal" },
        { source: "check", target: "drop", kind: "internal", label: "yes", sourceHandle: "yes", term: "drop" },
        { source: "check", target: "inbox", kind: "internal", label: "no", sourceHandle: "no", term: "inbox" },
        { source: "inbox", target: "rate", kind: "internal" },
        { source: "rate", target: "out", kind: "internal" },
        { source: "out", target: "disp", kind: "http" },
        { source: "retry", target: "dlq", kind: "internal", label: "exhaust", term: "dlq" },
      ],
    },
    objects: {
      nodes: [
        { id: "feed", label: "HttpLogTailFeeder", role: "GET /bus/events after offset", term: "http-log-tail-feeder" },
        { id: "dup", label: "Duplicator + Shuffler", role: "hostile, after log read", term: "duplicator-shuffler" },
        { id: "bus", label: "IEventBus", role: "port — not a Center", term: "bus" },
        { id: "cons", label: "AlarmConsumer", role: "handle.Squeaked", term: "alarm-consumer" },
        { id: "pool", label: "ShardWorkerPool", role: "hot key isolation", term: "shard-worker-pool" },
        { id: "seq", label: "PerKeySequencer", role: "order per duck", term: "per-key-sequencer" },
        { id: "retry", label: "RetryPipeline", role: "3x then DLQ", term: "retry-pipeline" },
        { id: "inbox", label: "Inbox", role: "dedupe EventId", term: "inbox" },
        { id: "store", label: "AlarmStore", role: "rate window + outbox", term: "alarm-store" },
        { id: "disp", label: "RemoteOutboxDispatcher", role: "POST /bus/events", term: "remote-outbox-dispatcher" },
      ],
      edges: [
        { source: "feed", target: "dup", label: "PublishAsync", kind: "http", term: "bus" },
        { source: "dup", target: "bus", label: "clone may duplicate", kind: "internal", term: "at-least-once" },
        { source: "bus", target: "cons", label: "Subscribe(alarm-center)", kind: "event", term: "consumer-group" },
        { source: "cons", target: "pool", label: "DispatchAsync", kind: "internal", term: "shard-worker-pool" },
        { source: "pool", target: "seq", label: "per PartitionKey", kind: "internal", term: "partition-key" },
        { source: "seq", target: "retry", label: "handle", kind: "internal" },
        { source: "retry", target: "inbox", label: "try", kind: "internal", term: "inbox" },
        { source: "inbox", target: "store", label: "one tx", kind: "internal", term: "alarm-store" },
        { source: "store", target: "disp", label: "outbox", kind: "http", term: "outbox" },
      ],
    },
    failure: "retry exhaust → DLQ + advance offset (partition continues)",
  },
  {
    id: "dashboard",
    title: "DashboardCenter",
    role: "Disposable CQRS projector",
    owns: "squeaks_by_duck_hour",
    term: "dashboard",
    docs: ["docs/architecture/step-5.md", "docs/architecture/step-8.md"],
    code: [
      { path: "src/DuckNet.DashboardCenter/DashboardConsumer.cs", why: "project Squeaked" },
      { path: "src/DuckNet.DashboardCenter/DashboardReadModel.cs", why: "hour buckets + volume_db" },
      { path: "src/DuckNet.Kernel/Consumer/Inbox.cs", why: "dedupe; no sequencer" },
    ],
    process: {
      nodes: [
        { id: "feed", label: "HttpLogTailFeeder", kind: "step", term: "http-log-tail-feeder" },
        { id: "dup", label: "Duplicator + Shuffler", kind: "step", note: "after log read", term: "duplicator-shuffler" },
        { id: "bus", label: "IEventBus", kind: "step", term: "bus" },
        { id: "shard", label: "ShardWorkerPool", kind: "step", term: "shard-worker-pool" },
        { id: "inbox", label: "inbox + squeaks_by_duck_hour", kind: "store", note: "one tx; commutative", term: "squeaks-by-duck-hour" },
        { id: "rebuild", label: "rebuild", kind: "step", note: "truncate, offset 0, replay", term: "rebuild" },
      ],
      edges: [
        { source: "feed", target: "dup", kind: "http" },
        { source: "dup", target: "bus", kind: "internal", label: "PublishAsync", term: "bus" },
        { source: "bus", target: "shard", kind: "event", label: "Squeaked", term: "squeaked" },
        { source: "shard", target: "inbox", kind: "internal" },
        { source: "rebuild", target: "inbox", kind: "internal", label: "from offset 0", term: "rebuild" },
      ],
    },
    objects: {
      nodes: [
        { id: "feed", label: "HttpLogTailFeeder", role: "GET /bus/events after offset", term: "http-log-tail-feeder" },
        { id: "dup", label: "Duplicator + Shuffler", role: "hostile, after log read", term: "duplicator-shuffler" },
        { id: "bus", label: "IEventBus", role: "port — not a Center", term: "bus" },
        { id: "cons", label: "DashboardConsumer", role: "project Squeaked", term: "dashboard-consumer" },
        { id: "pool", label: "ShardWorkerPool", role: "no PerKeySequencer", term: "shard-worker-pool" },
        { id: "inbox", label: "Inbox", role: "dedupe EventId", term: "inbox" },
        { id: "rm", label: "DashboardReadModel", role: "hour buckets + volume_db", term: "dashboard-read-model" },
      ],
      edges: [
        { source: "feed", target: "dup", label: "PublishAsync", kind: "http", term: "bus" },
        { source: "dup", target: "bus", label: "clone may duplicate", kind: "internal", term: "at-least-once" },
        { source: "bus", target: "cons", label: "Subscribe(dashboard-projector)", kind: "event", term: "consumer-group" },
        { source: "cons", target: "pool", label: "DispatchAsync", kind: "internal", term: "shard-worker-pool" },
        { source: "pool", target: "inbox", label: "one tx", kind: "internal", term: "inbox" },
        { source: "inbox", target: "rm", label: "upsert hour", kind: "internal", term: "squeaks-by-duck-hour" },
      ],
    },
    failure: "No PerKeySequencer — hour counts commute under shuffle. No outbox.",
  },
  {
    id: "billing",
    title: "BillingCenter",
    role: "Saga on alarm facts",
    owns: "billing_sagas",
    term: "billing",
    docs: ["docs/architecture/step-10.md"],
    code: [
      { path: "src/DuckNet.BillingCenter/BillingConsumer.cs", why: "AlarmRaised / AlarmResolved" },
      { path: "src/DuckNet.BillingCenter/BillingStore.cs", why: "Reserved | Released | Expired" },
      { path: "src/DuckNet.BillingCenter/SagaTimeoutWorker.cs", why: "timeout compensation" },
    ],
    process: {
      nodes: [
        { id: "feed", label: "HttpLogTailFeeder", kind: "step", term: "http-log-tail-feeder" },
        { id: "dup", label: "Duplicator + Shuffler", kind: "step", note: "after log read", term: "duplicator-shuffler" },
        { id: "bus", label: "IEventBus", kind: "step", term: "bus" },
        { id: "handle", label: "handle AlarmRaised / AlarmResolved", kind: "step", term: "billing-consumer" },
        { id: "saga", label: "billing_sagas", kind: "store", note: "Reserved | Released | Expired", term: "billing-sagas" },
        { id: "timeout", label: "timeout worker", kind: "step", note: "Aspire 15s", term: "saga-timeout-worker" },
        { id: "fast", label: "FeeReleased (AlarmResolved)", kind: "step", term: "fee-released" },
        { id: "slow", label: "Expired + FeeReleased (Timeout)", kind: "step", term: "expired" },
        { id: "out", label: "outbox FeeReserved / FeeReleased", kind: "step", term: "fee-reserved" },
        { id: "disp", label: "RemoteOutboxDispatcher", kind: "step", note: "POST /bus/events", term: "remote-outbox-dispatcher" },
      ],
      edges: [
        { source: "feed", target: "dup", kind: "http" },
        { source: "dup", target: "bus", kind: "internal", label: "PublishAsync", term: "bus" },
        { source: "bus", target: "handle", kind: "event" },
        { source: "handle", target: "saga", kind: "internal" },
        { source: "handle", target: "out", kind: "internal", label: "AlarmRaised → reserve", term: "fee-reserved" },
        { source: "saga", target: "timeout", kind: "internal" },
        { source: "handle", target: "fast", kind: "event", label: "AlarmResolved", term: "alarm-resolved" },
        { source: "timeout", target: "slow", kind: "internal", label: "still Reserved", term: "reserved" },
        { source: "fast", target: "out", kind: "internal" },
        { source: "slow", target: "out", kind: "internal" },
        { source: "out", target: "disp", kind: "http" },
      ],
    },
    objects: {
      nodes: [
        { id: "feed", label: "HttpLogTailFeeder", role: "GET /bus/events after offset", term: "http-log-tail-feeder" },
        { id: "cons", label: "BillingConsumer", role: "AlarmRaised / AlarmResolved", term: "billing-consumer" },
        { id: "store", label: "BillingStore", role: "saga PK = alarm_id", term: "billing-store" },
        { id: "tmo", label: "SagaTimeoutWorker", role: "15s in Aspire", term: "saga-timeout-worker" },
        { id: "disp", label: "RemoteOutboxDispatcher", role: "POST /bus/events", term: "remote-outbox-dispatcher" },
        { id: "bus", label: "IEventBus", role: "port — not a Center", term: "bus" },
      ],
      edges: [
        { source: "feed", target: "bus", label: "PublishAsync", kind: "http", term: "bus" },
        { source: "bus", target: "cons", label: "Subscribe(billing-center)", kind: "event", term: "consumer-group" },
        { source: "cons", target: "store", label: "handle(AlarmRaised)", kind: "internal", term: "alarm-raised" },
        { source: "store", target: "tmo", label: "start(timer)", kind: "internal", term: "saga-timeout-worker" },
        { source: "cons", target: "store", label: "handle(AlarmResolved)", kind: "event", term: "alarm-resolved" },
        { source: "tmo", target: "store", label: "compensate if Reserved", kind: "internal", term: "expired" },
        { source: "store", target: "disp", label: "outbox Fee*", kind: "http", term: "outbox" },
      ],
    },
  },
  {
    id: "bus",
    title: "IEventBus",
    role: "Port — not a Center",
    owns: "nothing (adapter)",
    term: "bus",
    docs: ["docs/architecture/step-11.md"],
    code: [
      { path: "src/DuckNet.EventBus/EventBusFactory.cs", why: "in-memory vs RabbitMQ" },
      { path: "src/DuckNet.EventBus/HttpLogClient.cs", why: "tail / append the log" },
      { path: "src/DuckNet.EventBus/RabbitMqEventBus.cs", why: "Aspire production path" },
      { path: "src/DuckNet.EventBus/InMemoryEventBus.cs", why: "tests and kernel" },
    ],
    process: {
      nodes: [
        { id: "http", label: "HttpLogClient / HttpLogTailFeeder", kind: "step", term: "http-log-client" },
        { id: "hostile", label: "Duplicator + Shuffler", kind: "step", note: "on the consumer, never before append", term: "duplicator-shuffler" },
        { id: "factory", label: "EventBusFactory.Create()", kind: "step", term: "event-bus-factory" },
        { id: "rmq", label: "RabbitMqEventBus or InMemoryEventBus", kind: "step", term: "rabbitmq-event-bus" },
        { id: "queue", label: "queue per consumer group", kind: "store", term: "consumer-group" },
      ],
      edges: [
        { source: "http", target: "hostile", kind: "http" },
        { source: "hostile", target: "factory", kind: "internal" },
        { source: "factory", target: "rmq", kind: "internal" },
        { source: "rmq", target: "queue", kind: "event", label: "fan-out", term: "consumer-group" },
      ],
    },
    objects: {
      nodes: [
        { id: "http", label: "HttpLogClient", role: "tail / append the log", term: "http-log-client" },
        { id: "factory", label: "EventBusFactory", role: "one-line composition", term: "event-bus-factory" },
        { id: "rmq", label: "RabbitMqEventBus", role: "Aspire production path", term: "rabbitmq-event-bus" },
        { id: "mem", label: "InMemoryEventBus", role: "tests and kernel", term: "in-memory-event-bus" },
      ],
      edges: [
        { source: "http", target: "factory", label: "PublishAsync after tail", kind: "http", term: "bus" },
        { source: "factory", target: "rmq", label: "connection string set", kind: "internal", term: "rabbitmq-event-bus" },
        { source: "factory", target: "mem", label: "connection string unset", kind: "internal", term: "in-memory-event-bus" },
      ],
    },
    failure: "Inbox — not the bus — is the dedupe. Duplicate EventId is at-least-once.",
  },
];

export const centerById = Object.fromEntries(centers.map((c) => [c.id, c])) as Record<
  CenterId,
  CenterMeta
>;

export const overviewCaption =
  "dup + shuffle run on each consumer after the log read. Centers never call each other; this UI does, as a browser.";

export function mapTermIds(): string[] {
  const ids = new Set<string>();
  for (const center of centers) {
    ids.add(center.term);
    for (const node of center.process.nodes) {
      if (node.term) {
        ids.add(node.term);
      }
    }
    for (const node of center.objects.nodes) {
      if (node.term) {
        ids.add(node.term);
      }
    }
    for (const edge of center.process.edges) {
      if (edge.term) {
        ids.add(edge.term);
      }
    }
    for (const edge of center.objects.edges) {
      if (edge.term) {
        ids.add(edge.term);
      }
    }
  }
  for (const edge of overviewGraph.edges) {
    if (edge.term) {
      ids.add(edge.term);
    }
  }
  return [...ids];
}
