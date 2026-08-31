import { computed } from "vue";
import { useQuery } from "@tanstack/vue-query";
import {
  catchUp,
  fetchCatalog,
  fetchCenterJson,
  fetchMetrics,
  fetchStats,
  type AlarmRow,
  type AlarmStats,
  type BillingStats,
  type DlqRow,
  type SagaRow,
  type TelemetryStats,
} from "./api";

export function useLiveCenters() {
  const catalog = useQuery({
    queryKey: ["ui", "catalog"],
    queryFn: fetchCatalog,
    staleTime: 60_000,
  });

  const telemetryUrl = computed(() => catalog.data.value?.telemetry ?? "");
  const alarmUrl = computed(() => catalog.data.value?.alarm ?? "");
  const billingUrl = computed(() => catalog.data.value?.billing ?? "");

  const { data: telemetryStats, error: telemetryError } = useQuery({
    queryKey: computed(() => ["live", "stats", "tel", telemetryUrl.value]),
    queryFn: () => fetchCenterJson<TelemetryStats>(telemetryUrl.value, "/stats"),
    enabled: computed(() => telemetryUrl.value.length > 0),
  });

  const { data: alarmStats, error: alarmError } = useQuery({
    queryKey: computed(() => ["live", "stats", "alm", alarmUrl.value]),
    queryFn: () => fetchCenterJson<AlarmStats>(alarmUrl.value, "/stats"),
    enabled: computed(() => alarmUrl.value.length > 0),
  });

  const { data: billingStats, error: billingError } = useQuery({
    queryKey: computed(() => ["live", "stats", "bil", billingUrl.value]),
    queryFn: () => fetchCenterJson<BillingStats>(billingUrl.value, "/stats"),
    enabled: computed(() => billingUrl.value.length > 0),
  });

  const { data: dashboardStats, error: dashboardError } = useQuery({
    queryKey: ["live", "stats", "dash"],
    queryFn: fetchStats,
  });

  const { data: dashboardMetrics } = useQuery({
    queryKey: ["live", "dashboard", "metrics"],
    queryFn: fetchMetrics,
  });

  const { data: alarms } = useQuery({
    queryKey: computed(() => ["live", "lists", "alarms", alarmUrl.value]),
    queryFn: () => fetchCenterJson<AlarmRow[]>(alarmUrl.value, "/alarms"),
    enabled: computed(() => alarmUrl.value.length > 0),
  });

  const { data: sagas } = useQuery({
    queryKey: computed(() => ["live", "lists", "sagas", billingUrl.value]),
    queryFn: () => fetchCenterJson<SagaRow[]>(billingUrl.value, "/sagas"),
    enabled: computed(() => billingUrl.value.length > 0),
  });

  const { data: alarmDlq } = useQuery({
    queryKey: computed(() => ["live", "lists", "dlq-alm", alarmUrl.value]),
    queryFn: () => fetchCenterJson<DlqRow[]>(alarmUrl.value, "/dlq"),
    enabled: computed(() => alarmUrl.value.length > 0),
  });

  const { data: dashboardDlq } = useQuery({
    queryKey: ["live", "lists", "dlq-dash"],
    queryFn: () => fetchCenterJson<DlqRow[]>("", "/dlq"),
  });

  const { data: billingDlq } = useQuery({
    queryKey: computed(() => ["live", "lists", "dlq-bil", billingUrl.value]),
    queryFn: () => fetchCenterJson<DlqRow[]>(billingUrl.value, "/dlq"),
    enabled: computed(() => billingUrl.value.length > 0),
  });

  const logHead = computed(() => telemetryStats.value?.lastOffset);

  const snapshot = computed(() => ({
    telemetryUrl: telemetryUrl.value,
    alarmUrl: alarmUrl.value,
    billingUrl: billingUrl.value,
    telemetry: telemetryStats.value,
    telemetryError: telemetryError.value,
    alarm: alarmStats.value,
    alarmError: alarmError.value,
    billing: billingStats.value,
    billingError: billingError.value,
    dashboard: dashboardStats.value,
    dashboardError: dashboardError.value,
    dashboardMetrics: dashboardMetrics.value,
    alarms: alarms.value ?? [],
    sagas: sagas.value ?? [],
    alarmDlq: alarmDlq.value ?? [],
    dashboardDlq: dashboardDlq.value ?? [],
    billingDlq: billingDlq.value ?? [],
    alarmCatchUp: catchUp(logHead.value, alarmStats.value?.lastOffset),
    dashboardCatchUp: catchUp(logHead.value, dashboardStats.value?.lastOffset),
    billingCatchUp: catchUp(logHead.value, billingStats.value?.lastOffset),
  }));

  return { snapshot };
}

export type LiveSnapshot = ReturnType<typeof useLiveCenters>["snapshot"] extends infer C
  ? C extends { value: infer T }
    ? T
    : never
  : never;
