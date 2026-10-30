<template>
  <div class="login-page">
    <v-card width="420" max-width="92vw" rounded="xl" border flat class="pa-7">
      <div class="d-flex align-center ga-3 mb-7"><div class="brand-mark">RB</div><div><div class="brand-name text-grey-darken-4">RaceBib</div><div class="brand-caption text-grey">RACE OPERATIONS</div></div></div>
      <h1 class="text-h5 font-weight-bold">Welcome back</h1><p class="text-body-2 text-medium-emphasis mt-1 mb-6">Sign in to manage your race events.</p>
      <v-text-field label="Email address" type="email" variant="outlined" />
      <v-text-field label="Password" type="password" variant="outlined" />
      <v-btn block color="primary" size="large">Sign in</v-btn>
      <p class="text-caption text-medium-emphasis mt-5">Authentication will be connected when the API login and session endpoints are implemented.</p>
    </v-card>
  </div>
</template>
