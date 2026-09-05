<script setup lang="ts">
import { computed } from "vue";
import { useMutation, useQuery, useQueryClient } from "@tanstack/vue-query";
import {
  fetchCatalog,
  fetchCatalogParts,
  fetchFleet,
  fetchOrders,
  fetchServiceCases,
  postCenter,
  type CatalogPartRow,
  type FleetHealthRow,
  type ServiceCaseRow,
} from "../api";

const queryClient = useQueryClient();

const { data: catalog } = useQuery({
  queryKey: ["ui", "catalog"],
  queryFn: fetchCatalog,
});

const { data: fleet } = useQuery({
  queryKey: ["dashboard", "fleet"],
  queryFn: fetchFleet,
});

const { data: cases } = useQuery({
  queryKey: ["dashboard", "service-cases"],
  queryFn: fetchServiceCases,
});

const { data: orders } = useQuery({
  queryKey: ["dashboard", "orders"],
  queryFn: fetchOrders,
});

const { data: parts } = useQuery({
  queryKey: ["dashboard", "catalog"],
  queryFn: fetchCatalogParts,
});

const billingBase = computed(() => catalog.value?.billing ?? "");

const {
  mutate: command,
  isPending: commandPending,
  error: commandError,
} = useMutation({
  mutationFn: (path: string) => postCenter(billingBase.value, path),
  onSuccess: () => {
    void queryClient.invalidateQueries({ queryKey: ["dashboard"] });
  },
});

function money(cents: number): string {
  return (cents / 100).toLocaleString(undefined, { style: "currency", currency: "USD" });
}

function scoreClass(score: number): string {
  if (score >= 0.7) {
    return "text-danger";
  }
  if (score >= 0.5) {
    return "text-warning";
  }
  return "text-success";
}

function partName(sku: string, catalogParts: CatalogPartRow[] | undefined): string {
  return catalogParts?.find((p) => p.sku === sku)?.name ?? sku;
}

function accept(row: ServiceCaseRow) {
  command(`/service-cases/${row.alarmId}/accept`);
}

function decline(row: ServiceCaseRow) {
  command(`/service-cases/${row.alarmId}/decline`);
}

function pick(row: ServiceCaseRow) {
  command(`/service-cases/${row.alarmId}/pick`);
}

function ship(row: ServiceCaseRow) {
  command(`/service-cases/${row.alarmId}/ship`);
}

const reserved = computed(() => (cases.value ?? []).filter((c) => c.state === "Reserved"));
</script>

<template>
  <div class="container-fluid px-4 pb-5">
    <p class="text-secondary mb-4">
      Planner board. Accept and decline go to Billing from this browser — Dashboard never calls Billing as a Center.
    </p>
    <div v-if="commandError" class="alert alert-danger">{{ commandError.message }}</div>

    <h2 class="h5">Fleet health</h2>
    <div class="table-responsive mb-4">
      <table class="table table-sm table-duck">
        <thead>
          <tr>
            <th>Asset</th>
            <th>Score</th>
            <th>Vibration</th>
            <th>Temp</th>
            <th>Hours</th>
            <th>Recommended</th>
          </tr>
        </thead>
        <tbody>
          <tr v-for="row in fleet ?? []" :key="row.assetId">
            <td>{{ row.assetId }}</td>
            <td :class="scoreClass(row.score)">{{ row.score.toFixed(2) }}</td>
            <td>{{ row.vibrationMmS.toFixed(1) }} mm/s</td>
            <td>{{ row.temperatureC.toFixed(0) }} °C</td>
            <td>{{ row.engineHours.toFixed(0) }}</td>
            <td>{{ row.recommendedService }}</td>
          </tr>
          <tr v-if="!(fleet ?? []).length">
            <td colspan="6" class="text-secondary">No predictions yet.</td>
          </tr>
        </tbody>
      </table>
    </div>

    <h2 class="h5">Service cases</h2>
    <div class="table-responsive mb-4">
      <table class="table table-sm table-duck">
        <thead>
          <tr>
            <th>Asset</th>
            <th>State</th>
            <th>Total</th>
            <th></th>
          </tr>
        </thead>
        <tbody>
          <tr v-for="row in cases ?? []" :key="row.alarmId">
            <td>{{ row.assetId }}</td>
            <td>{{ row.state }}</td>
            <td>{{ money(row.totalCents) }}</td>
            <td class="text-nowrap">
              <button
                v-if="row.state === 'Reserved'"
                class="btn btn-sm btn-warning me-1"
                :disabled="commandPending || !billingBase"
                @click="accept(row)"
              >
                Accept
              </button>
              <button
                v-if="row.state === 'Reserved'"
                class="btn btn-sm btn-outline-secondary me-1"
                :disabled="commandPending || !billingBase"
                @click="decline(row)"
              >
                Decline
              </button>
              <button
                v-if="row.state === 'Confirmed'"
                class="btn btn-sm btn-outline-warning me-1"
                :disabled="commandPending || !billingBase"
                @click="pick(row)"
              >
                Pick
              </button>
              <button
                v-if="row.state === 'Picked'"
                class="btn btn-sm btn-outline-warning"
                :disabled="commandPending || !billingBase"
                @click="ship(row)"
              >
                Ship
              </button>
            </td>
          </tr>
          <tr v-if="!(cases ?? []).length">
            <td colspan="4" class="text-secondary">No service cases.</td>
          </tr>
        </tbody>
      </table>
    </div>

    <h2 class="h5">Orders</h2>
    <ul class="list-group mb-4">
      <li v-for="row in orders ?? []" :key="row.orderId" class="list-group-item d-flex justify-content-between">
        <span>{{ row.assetId }}</span>
        <span>{{ money(row.totalCents) }}</span>
      </li>
      <li v-if="!(orders ?? []).length" class="list-group-item text-secondary">No confirmed orders.</li>
    </ul>

    <h2 class="h5">Catalog (projected)</h2>
    <p class="text-secondary small">Names arrive as <code>PartCatalogPublished</code> — Dashboard does not open billing.db.</p>
    <ul class="list-group">
      <li v-for="part in parts ?? []" :key="part.sku" class="list-group-item d-flex justify-content-between">
        <span>{{ partName(part.sku, parts) }} <span class="text-secondary">({{ part.sku }})</span></span>
        <span>{{ money(part.unitCents) }}</span>
      </li>
    </ul>

    <p v-if="reserved.length" class="text-secondary small mt-3">
      {{ reserved.length }} reserved case(s) waiting on a planner.
    </p>
  </div>
</template>
