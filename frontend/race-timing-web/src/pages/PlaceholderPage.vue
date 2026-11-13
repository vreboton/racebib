<template><div class="page-heading"><div><div class="eyebrow">ADMINISTRATION</div><h1>{{ title }}</h1><p>This workspace will be implemented as its backend capabilities are completed.</p></div></div></template>
<script setup lang="ts">
import { computed } from 'vue'
import { useRoute } from 'vue-router'
const route = useRoute()
const title = computed(() => String(route.meta.title || 'Administration'))
</script>
