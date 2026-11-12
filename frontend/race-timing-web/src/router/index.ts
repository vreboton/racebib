import { createRouter, createWebHistory } from 'vue-router'
import AppLayout from '../layouts/AppLayout.vue'
import DashboardPage from '../pages/DashboardPage.vue'
import EventsPage from '../pages/EventsPage.vue'
import EventFormPage from '../pages/EventFormPage.vue'
import EventDetailsPage from '../pages/EventDetailsPage.vue'
import { pinia } from '../stores'
import { useAuthStore } from '../stores/auth'

const router = createRouter({
  history: createWebHistory(),
  routes: [
    { path: '/login', name: 'login', component: () => import('../pages/LoginPage.vue'), meta: { public: true } },
    { path: '/accept-invitation', name: 'accept-invitation', component: () => import('../pages/AcceptInvitationPage.vue'), meta: { public: true } },
    {
      path: '/',
      component: AppLayout,
      children: [
        { path: '', name: 'dashboard', component: DashboardPage },
        { path: 'events', name: 'events', component: EventsPage },
        { path: 'events/new', name: 'event-create', component: EventFormPage },
        { path: 'events/:id', name: 'event-details', component: EventDetailsPage },
        { path: 'events/:id/edit', name: 'event-edit', component: EventFormPage },
        { path: 'participants', name: 'participants', component: () => import('../pages/PlaceholderPage.vue'), meta: { title: 'Participants' } },
        { path: 'courses', name: 'courses', component: () => import('../pages/PlaceholderPage.vue'), meta: { title: 'Courses' } },
        { path: 'categories', name: 'categories', component: () => import('../pages/PlaceholderPage.vue'), meta: { title: 'Categories' } },
        { path: 'users', name: 'users', component: () => import('../pages/OrganizerMembersPage.vue'), meta: { title: 'Organizer Members' } },
        { path: 'settings', name: 'settings', component: () => import('../pages/PlaceholderPage.vue'), meta: { title: 'System Settings' } },
      ],
    },
  ],
})

router.beforeEach(async (to) => {
  const auth = useAuthStore(pinia)
  await auth.initialize()

  if (to.meta.public) {
    if (to.name === 'login' && auth.isAuthenticated) return { name: 'dashboard' }
    return true
  }

  if (!auth.isAuthenticated) return { name: 'login', query: { redirect: to.fullPath } }
  return true
})

export default router
