<script setup lang="ts">
import { computed, ref } from "vue";
import { useMutation, useQuery, useQueryClient } from "@tanstack/vue-query";
import {
  FlexRender,
  createColumnHelper,
  getCoreRowModel,
  getFilteredRowModel,
  getSortedRowModel,
  useVueTable,
  type SortingState,
} from "@tanstack/vue-table";
import {
  fetchMetrics,
  fetchStats,
  fetchSummary,
  rebuildDashboard,
  type SqueakHourRow,
} from "../api";

const queryClient = useQueryClient();
const duckFilter = ref("");
const sorting = ref<SortingState>([{ id: "count", desc: true }]);

const {
  data: summary,
  isPending: summaryPending,
  isFetching: summaryFetching,
  error: summaryError,
} = useQuery({
  queryKey: ["dashboard", "summary"],
  queryFn: fetchSummary,
});

const {
  data: stats,
  error: statsError,
} = useQuery({
  queryKey: ["dashboard", "stats"],
  queryFn: fetchStats,
});

const { data: metrics } = useQuery({
  queryKey: ["dashboard", "metrics"],
  queryFn: fetchMetrics,
});

const {
  mutate: runRebuild,
  isPending: rebuildPending,
  error: rebuildError,
} = useMutation({
  mutationFn: rebuildDashboard,
  onSuccess: () => {
    void queryClient.invalidateQueries({ queryKey: ["dashboard"] });
    void queryClient.invalidateQueries({ queryKey: ["live"] });
  },
});

const rows = computed(() => summary.value?.rows ?? []);
const totalSqueaks = computed(
  () => summary.value?.totalSqueaks ?? stats.value?.totalSqueaks ?? 0,
);
const rowCount = computed(
  () => summary.value?.rowCount ?? stats.value?.rowCount ?? 0,
);
const totalVolume = computed(
  () => summary.value?.totalVolumeDb ?? stats.value?.totalVolumeDb ?? 0,
);
const lastOffset = computed(() => stats.value?.lastOffset ?? 0);
const shards = computed(() => metrics.value?.shards ?? stats.value?.shards ?? []);
const keyLags = computed(() => metrics.value?.keys ?? stats.value?.keys ?? []);
const hottest = computed(() =>
  [...keyLags.value].sort((a, b) => b.maxLagMs - a.maxLagMs)[0],
);
const avgDb = computed(() =>
  totalSqueaks.value > 0 ? totalVolume.value / totalSqueaks.value : 0,
);
const maxVolume = computed(() =>
  rows.value.reduce((max, row) => Math.max(max, row.volumeDb), 0),
);

const columnHelper = createColumnHelper<SqueakHourRow>();
const columns = [
  columnHelper.accessor("duckId", { header: "Duck", cell: (info) => info.getValue() }),
  columnHelper.accessor("hourUtc", { header: "Hour (UTC)", cell: (info) => info.getValue() }),
  columnHelper.accessor("count", {
    header: "Squeaks",
    cell: (info) => info.getValue().toLocaleString(),
  }),
  columnHelper.accessor("volumeDb", {
    header: "Volume sum (dB)",
    cell: (info) => info.getValue().toFixed(1),
  }),
  columnHelper.display({
    id: "avgDb",
    header: "Avg dB",
    cell: (info) => {
      const row = info.row.original;
      return row.count > 0 ? (row.volumeDb / row.count).toFixed(1) : "—";
    },
  }),
  columnHelper.display({
    id: "volumeBar",
    header: "Share",
    enableSorting: false,
    cell: (info) => info.row.original.volumeDb,
  }),
];

const table = useVueTable({
  get data() {
    return rows.value;
  },
  columns,
  state: {
    get sorting() {
      return sorting.value;
    },
    get globalFilter() {
      return duckFilter.value;
    },
  },
  onSortingChange: (updater) => {
    sorting.value = typeof updater === "function" ? updater(sorting.value) : updater;
  },
  onGlobalFilterChange: (updater) => {
    duckFilter.value = typeof updater === "function" ? updater(duckFilter.value) : updater;
  },
  globalFilterFn: (row, _columnId, filter) => {
    if (!filter) {
      return true;
    }
    return row.original.duckId.toLowerCase().includes(String(filter).toLowerCase());
  },
  getCoreRowModel: getCoreRowModel(),
  getSortedRowModel: getSortedRowModel(),
  getFilteredRowModel: getFilteredRowModel(),
});

function sortMark(id: string): string {
  const sorted = table.getColumn(id)?.getIsSorted();
  if (sorted === "asc") {
    return "bi-caret-up-fill";
  }
  if (sorted === "desc") {
    return "bi-caret-down-fill";
  }
  return "bi-caret-up";
}

function volumeWidth(volume: number): string {
  if (maxVolume.value <= 0) {
    return "0%";
  }
  return `${Math.max(4, (volume / maxVolume.value) * 100)}%`;
}

function formatNumber(value: number, digits = 0): string {
  return value.toLocaleString(undefined, {
    maximumFractionDigits: digits,
    minimumFractionDigits: digits,
  });
}

const errorMessage = computed(
  () => summaryError.value?.message ?? statsError.value?.message ?? rebuildError.value?.message,
);
</script>

<template>
  <main class="container-fluid px-4 pb-5">
    <div class="d-flex flex-wrap align-items-center justify-content-between gap-3 mb-4">
      <div class="text-secondary small d-flex align-items-center gap-2">
        <span class="live-dot" aria-hidden="true"></span>
        {{ summaryFetching ? "syncing" : "live" }}
        <span class="font-monospace">offset {{ lastOffset }}</span>
      </div>
      <button
        class="btn btn-sm btn-outline-warning"
        data-bs-toggle="modal"
        data-bs-target="#rebuildModal"
        :disabled="rebuildPending"
      >
        <i class="bi bi-arrow-repeat me-1"></i>
        Rebuild
      </button>
    </div>

    <div v-if="errorMessage" class="alert alert-danger" role="alert">
      {{ errorMessage }}
    </div>

    <div class="row g-3 mb-4">
      <div class="col-6 col-xl-3">
        <div class="card stat-card h-100">
          <div class="card-body">
            <div class="text-secondary small text-uppercase">Squeaks</div>
            <div class="stat-value fs-3">{{ formatNumber(totalSqueaks) }}</div>
          </div>
        </div>
      </div>
      <div class="col-6 col-xl-3">
        <div class="card stat-card h-100">
          <div class="card-body">
            <div class="text-secondary small text-uppercase">Hour buckets</div>
            <div class="stat-value fs-3">{{ formatNumber(rowCount) }}</div>
          </div>
        </div>
      </div>
      <div class="col-6 col-xl-3">
        <div class="card stat-card h-100">
          <div class="card-body">
            <div class="text-secondary small text-uppercase">Volume sum</div>
            <div class="stat-value fs-3">{{ formatNumber(totalVolume, 0) }} dB</div>
          </div>
        </div>
      </div>
      <div class="col-6 col-xl-3">
        <div class="card stat-card h-100">
          <div class="card-body">
            <div class="text-secondary small text-uppercase">Avg / squeak</div>
            <div class="stat-value fs-3">{{ formatNumber(avgDb, 1) }} dB</div>
          </div>
        </div>
      </div>
    </div>

    <div v-if="shards.length > 0" class="row g-3 mb-4">
      <div v-for="shard in shards" :key="shard.id" class="col-12 col-md-4">
        <div class="card stat-card h-100" :class="{ 'stat-card-hot': shard.queued > 0 || shard.lag > 0 }">
          <div class="card-body">
            <div class="text-secondary small text-uppercase">Shard {{ shard.id }}</div>
            <div class="stat-value fs-4">q {{ shard.queued }} · lag {{ shard.lag }}</div>
            <div class="text-secondary small mt-1">
              processed {{ formatNumber(shard.processed) }}
              · bp {{ shard.backpressure }}
            </div>
          </div>
        </div>
      </div>
    </div>
    <p v-if="hottest" class="text-secondary small mb-4">
      Hottest key <code>{{ hottest.partitionKey }}</code>
      on shard {{ hottest.shard }} · max lag {{ formatNumber(hottest.maxLagMs) }} ms
    </p>

    <div class="card border-0 shadow-sm">
      <div class="card-header bg-white d-flex flex-wrap gap-2 align-items-center justify-content-between">
        <div>
          <strong>squeaks_by_duck_hour</strong>
          <span class="text-secondary small ms-2">TanStack Table · sortable · filterable</span>
        </div>
        <div class="input-group input-group-sm" style="max-width: 16rem">
          <span class="input-group-text"><i class="bi bi-search"></i></span>
          <input
            v-model="duckFilter"
            type="search"
            class="form-control"
            placeholder="Filter duck id"
            aria-label="Filter duck id"
          />
        </div>
      </div>
      <div class="table-responsive">
        <table class="table table-hover table-striped table-duck mb-0">
          <thead>
            <tr v-for="headerGroup in table.getHeaderGroups()" :key="headerGroup.id">
              <th
                v-for="header in headerGroup.headers"
                :key="header.id"
                :colspan="header.colSpan"
                @click="header.column.getToggleSortingHandler()?.($event)"
              >
                <span class="d-inline-flex align-items-center gap-1">
                  <FlexRender
                    :render="header.column.columnDef.header"
                    :props="header.getContext()"
                  />
                  <i
                    v-if="header.column.getCanSort()"
                    class="bi small"
                    :class="sortMark(header.column.id)"
                  ></i>
                </span>
              </th>
            </tr>
          </thead>
          <tbody>
            <template v-if="summaryPending">
              <tr>
                <td colspan="6" class="text-center text-secondary py-4">Loading projection…</td>
              </tr>
            </template>
            <template v-else-if="table.getRowModel().rows.length === 0">
              <tr>
                <td colspan="6" class="text-center text-secondary py-4">No hour buckets yet.</td>
              </tr>
            </template>
            <template v-else>
              <tr v-for="row in table.getRowModel().rows" :key="row.id">
              <td v-for="cell in row.getVisibleCells()" :key="cell.id">
                <div v-if="cell.column.id === 'volumeBar'" class="progress volume-bar">
                  <div
                    class="progress-bar"
                    role="progressbar"
                    :style="{ width: volumeWidth(row.original.volumeDb) }"
                  ></div>
                </div>
                <FlexRender
                  v-else
                  :render="cell.column.columnDef.cell"
                  :props="cell.getContext()"
                />
              </td>
            </tr>
            </template>
          </tbody>
        </table>
      </div>
    </div>
    <p class="text-secondary small mt-3 mb-0">
      Disposable CQRS read model. Rebuild truncates this table, resets the offset, and replays the log.
      v1 rows contribute 0 dB; live traffic is Squeaked v2.
    </p>
  </main>

  <div id="rebuildModal" class="modal fade" tabindex="-1" aria-labelledby="rebuildTitle" aria-hidden="true">
    <div class="modal-dialog">
      <div class="modal-content">
        <div class="modal-header">
          <h5 id="rebuildTitle" class="modal-title">Rebuild from the log</h5>
          <button type="button" class="btn-close" data-bs-dismiss="modal" aria-label="Close"></button>
        </div>
        <div class="modal-body">
          Truncate <code>squeaks_by_duck_hour</code>, clear the inbox, reset offset to 0, and replay.
          Counts should come back identical. Volume for historical v1 rows stays 0.
        </div>
        <div class="modal-footer">
          <button type="button" class="btn btn-outline-secondary" data-bs-dismiss="modal">Cancel</button>
          <button
            type="button"
            class="btn btn-warning"
            data-bs-dismiss="modal"
            :disabled="rebuildPending"
            @click="runRebuild()"
          >
            Replay from offset 0
          </button>
        </div>
      </div>
    </div>
  </div>
</template>
