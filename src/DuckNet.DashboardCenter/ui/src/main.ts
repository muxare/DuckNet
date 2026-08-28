import { createApp } from "vue";
import { VueQueryPlugin, QueryClient } from "@tanstack/vue-query";
import "bootstrap/dist/css/bootstrap.min.css";
import "bootstrap-icons/font/bootstrap-icons.css";
import "bootstrap/dist/js/bootstrap.bundle.min.js";
import "./styles.css";
import App from "./App.vue";

const queryClient = new QueryClient({
  defaultOptions: {
    queries: {
      refetchInterval: 2000,
      refetchOnWindowFocus: true,
    },
  },
});

createApp(App).use(VueQueryPlugin, { queryClient }).mount("#app");
