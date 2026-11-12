<template>
  <section>
    <div class="d-flex align-center justify-space-between mb-5">
      <div><h1 class="text-h4">Organizer Members</h1><p class="text-medium-emphasis mt-1">Manage access and invitations for an organizer.</p></div>
      <v-select v-model="organizerId" :items="organizers" item-title="name" item-value="id" label="Organizer" density="compact" hide-details style="max-width: 300px" @update:model-value="loadAll" />
    </div>

    <v-alert v-if="pageError" type="error" variant="tonal" class="mb-4">{{ pageError }}</v-alert>
    <v-alert v-if="!loadingOrganizers && !organizers.length" type="info" variant="tonal">You do not have permission to manage any organizer memberships.</v-alert>
    <template v-if="organizerId">
      <v-alert v-if="!auth.canManageMembers(organizerId)" type="error" variant="tonal" class="mb-4">Access denied. An Event Administrator for this organizer or a System Administrator is required.</v-alert>
      <template v-else>
        <v-card class="mb-6" rounded="lg">
          <v-card-title class="d-flex align-center">Members <v-spacer /><v-btn color="primary" prepend-icon="mdi-account-plus" @click="addDialog = true">Add existing user</v-btn><v-btn class="ml-2" variant="tonal" prepend-icon="mdi-email-plus" @click="inviteDialog = true">Invite member</v-btn></v-card-title>
          <v-card-text>
            <div class="d-flex ga-3 mb-4"><v-text-field v-model="search" label="Search name or email" prepend-inner-icon="mdi-magnify" clearable hide-details density="compact" @keyup.enter="loadMembers" /><v-btn @click="loadMembers">Search</v-btn></div>
            <v-progress-linear v-if="loadingMembers" indeterminate color="primary" />
            <v-alert v-else-if="membersError" type="error" variant="tonal">{{ membersError }}</v-alert>
            <v-alert v-else-if="!members.items.length" type="info" variant="tonal">No members found.</v-alert>
            <v-table v-else>
              <thead><tr><th>Member</th><th>Email</th><th>Role</th><th>Status</th><th>Joined</th><th class="text-right">Actions</th></tr></thead>
              <tbody><tr v-for="member in members.items" :key="member.membershipId">
                <td>{{ member.displayName || '—' }}</td><td>{{ member.email || '—' }}</td><td><v-chip size="small" :color="member.role === 'EventAdministrator' ? 'deep-purple' : 'blue'">{{ roleLabel(member.role) }}</v-chip></td>
                <td><v-chip size="small" :color="member.isActive ? 'success' : 'grey'">{{ member.isActive ? 'Active' : 'Inactive' }}</v-chip></td><td>{{ formatDate(member.joinedAt) }}</td>
                <td class="text-right"><v-btn size="small" variant="text" @click="askRoleChange(member)">Change role</v-btn><v-btn size="small" variant="text" :color="member.isActive ? 'error' : 'success'" @click="askStatusChange(member)">{{ member.isActive ? 'Deactivate' : 'Reactivate' }}</v-btn></td>
              </tr></tbody>
            </v-table>
            <div v-if="members.total > pageSize" class="d-flex justify-end align-center mt-3 ga-3"><span>Page {{ page }} of {{ Math.ceil(members.total / pageSize) }}</span><v-btn icon="mdi-chevron-left" variant="text" :disabled="page <= 1" @click="changePage(-1)"/><v-btn icon="mdi-chevron-right" variant="text" :disabled="page >= Math.ceil(members.total / pageSize)" @click="changePage(1)"/></div>
          </v-card-text>
        </v-card>

        <v-card rounded="lg"><v-card-title>Invitations</v-card-title><v-card-text>
          <v-progress-linear v-if="loadingInvitations" indeterminate color="primary" />
          <v-alert v-else-if="!invitations.length" type="info" variant="tonal">No invitations have been sent.</v-alert>
          <v-table v-else><thead><tr><th>Email</th><th>Role</th><th>Status</th><th>Expires</th><th class="text-right">Actions</th></tr></thead><tbody>
            <tr v-for="invitation in invitations" :key="invitation.id"><td>{{ invitation.email }}</td><td>{{ roleLabel(invitation.role) }}</td><td><v-chip size="small" :color="invitation.status === 'Pending' ? 'orange' : 'grey'">{{ invitation.status }}</v-chip></td><td>{{ formatDate(invitation.expiresAtUtc) }}</td><td class="text-right"><template v-if="invitation.status === 'Pending'"><v-btn size="small" variant="text" @click="resend(invitation.id)">Resend</v-btn><v-btn size="small" variant="text" color="error" @click="revoke(invitation.id)">Revoke</v-btn></template></td></tr>
          </tbody></v-table>
        </v-card-text></v-card>
      </template>
    </template>

    <v-dialog v-model="addDialog" max-width="520"><v-card title="Add an existing RaceBib user"><v-card-text><v-text-field v-model="userQuery" label="Search by name or email" hint="Enter at least three characters" persistent-hint @update:model-value="searchUsers"/><v-autocomplete v-model="selectedUserId" :items="userMatches" item-title="label" item-value="id" label="User" class="mt-3"/><v-select v-model="selectedRole" :items="roleOptions" label="Organizer role"/></v-card-text><v-card-actions><v-spacer/><v-btn @click="addDialog = false">Cancel</v-btn><v-btn color="primary" :disabled="!selectedUserId" :loading="saving" @click="addExisting">Add member</v-btn></v-card-actions></v-card></v-dialog>
    <v-dialog v-model="inviteDialog" max-width="520"><v-card title="Invite a member"><v-card-text><v-text-field v-model="inviteEmail" label="Email address" type="email"/><v-select v-model="inviteRole" :items="roleOptions" label="Organizer role"/><v-alert v-if="deliveryUrl" type="success" variant="tonal" class="mt-2">Development invitation link: <a :href="deliveryUrl">Open invitation</a></v-alert></v-card-text><v-card-actions><v-spacer/><v-btn @click="inviteDialog = false">Close</v-btn><v-btn color="primary" :loading="saving" @click="sendInvite">Send invitation</v-btn></v-card-actions></v-card></v-dialog>
    <v-dialog v-model="confirmDialog" max-width="440"><v-card :title="confirmTitle"><v-card-text>{{ confirmMessage }}</v-card-text><v-card-actions><v-spacer/><v-btn @click="confirmDialog = false">Cancel</v-btn><v-btn color="primary" :loading="saving" @click="confirmAction">Confirm</v-btn></v-card-actions></v-card></v-dialog>
    <v-snackbar v-model="snackbar" :color="snackbarColor" timeout="4000">{{ snackbarText }}</v-snackbar>
  </section>
</template>

<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { isAxiosError } from 'axios'
import { useAuthStore } from '../stores/auth'
import { organizerMembersApi, type Invitation, type Member, type Organizer, type OrganizerRole, type Page } from '../services/organizerMembers'

const auth = useAuthStore()
const organizers = ref<Organizer[]>([]), organizerId = ref(''), loadingOrganizers = ref(true)
const pageError = ref(''), membersError = ref(''), loadingMembers = ref(false), loadingInvitations = ref(false), saving = ref(false)
const search = ref(''), page = ref(1), pageSize = 20
const members = ref<Page<Member>>({ items: [], page: 1, pageSize, total: 0 }), invitations = ref<Invitation[]>([])
const roleOptions = [{ title: 'Event Administrator', value: 'EventAdministrator' }, { title: 'Race Director', value: 'RaceDirector' }]
const addDialog = ref(false), inviteDialog = ref(false), userQuery = ref(''), userMatches = ref<Array<{ id: string; label: string; email: string }>>([]), selectedUserId = ref(''), selectedRole = ref<OrganizerRole>('RaceDirector')
const inviteEmail = ref(''), inviteRole = ref<OrganizerRole>('RaceDirector'), deliveryUrl = ref('')
const confirmDialog = ref(false), confirmTitle = ref(''), confirmMessage = ref(''), pendingAction = ref<(() => Promise<void>) | null>(null)
const snackbar = ref(false), snackbarText = ref(''), snackbarColor = ref('success')
const allowedOrganizers = computed(() => organizers.value.filter(o => auth.canManageMembers(o.id)))

onMounted(async () => {
  try {
    const { data } = await organizerMembersApi.organizers()
    organizers.value = data
    if (!organizerId.value) organizerId.value = allowedOrganizers.value[0]?.id ?? ''
    await loadAll()
  } catch (error) { pageError.value = errorMessage(error) }
  finally { loadingOrganizers.value = false }
})
watch(organizerId, () => { page.value = 1 })
async function loadAll() { if (!organizerId.value || !auth.canManageMembers(organizerId.value)) return; await Promise.all([loadMembers(), loadInvitations()]) }
async function loadMembers() {
  if (!organizerId.value) return
  loadingMembers.value = true; membersError.value = ''
  try { members.value = (await organizerMembersApi.members(organizerId.value, { search: search.value || undefined, page: page.value, pageSize })).data }
  catch (error) { membersError.value = errorMessage(error) }
  finally { loadingMembers.value = false }
}
async function loadInvitations() { loadingInvitations.value = true; try { invitations.value = (await organizerMembersApi.invitations(organizerId.value)).data } catch (error) { pageError.value = errorMessage(error) } finally { loadingInvitations.value = false } }
async function searchUsers(value: string) {
  if (!organizerId.value || value.trim().length < 3) { userMatches.value = []; return }
  try { userMatches.value = (await organizerMembersApi.searchUsers(organizerId.value, value.trim())).data.map(u => ({ id: u.id, label: `${u.displayName || 'User'} — ${u.email || ''}`, email: u.email || '' })) }
  catch (error) { notify(errorMessage(error), 'error') }
}
async function addExisting() {
  const match = userMatches.value.find(u => u.id === selectedUserId.value); if (!match) return
  saving.value = true
  try { await organizerMembersApi.addMember(organizerId.value, match.email, selectedRole.value); addDialog.value = false; notify('Member added.'); await loadMembers() }
  catch (error) { notify(errorMessage(error), 'error') } finally { saving.value = false }
}
async function sendInvite() {
  saving.value = true; deliveryUrl.value = ''
  try { const { data } = await organizerMembersApi.invite(organizerId.value, inviteEmail.value, inviteRole.value); deliveryUrl.value = data.developmentAcceptanceUrl || ''; inviteEmail.value = ''; notify('Invitation sent.'); await loadInvitations() }
  catch (error) { notify(errorMessage(error), 'error') } finally { saving.value = false }
}
function askRoleChange(member: Member) { confirmTitle.value = 'Change member role'; confirmMessage.value = `Change ${member.email || member.displayName}'s role?`; pendingAction.value = async () => { await organizerMembersApi.changeRole(organizerId.value, member.membershipId, member.role === 'EventAdministrator' ? 'RaceDirector' : 'EventAdministrator'); await loadMembers() }; confirmDialog.value = true }
function askStatusChange(member: Member) { const active = !member.isActive; confirmTitle.value = active ? 'Reactivate membership' : 'Deactivate membership'; confirmMessage.value = `${active ? 'Restore' : 'Remove'} this member’s access to the organizer?`; pendingAction.value = async () => { await organizerMembersApi.setStatus(organizerId.value, member.membershipId, active); await loadMembers() }; confirmDialog.value = true }
async function confirmAction() { saving.value = true; try { await pendingAction.value?.(); confirmDialog.value = false; notify('Membership updated.') } catch (error) { notify(errorMessage(error), 'error') } finally { saving.value = false } }
async function resend(id: string) { try { const { data } = await organizerMembersApi.resend(organizerId.value, id); if (data.developmentAcceptanceUrl) { deliveryUrl.value = data.developmentAcceptanceUrl; inviteDialog.value = true }; notify('Invitation resent.') } catch (error) { notify(errorMessage(error), 'error') } }
function revoke(id: string) { confirmTitle.value = 'Revoke invitation'; confirmMessage.value = 'The invitation link will no longer work.'; pendingAction.value = async () => { await organizerMembersApi.revoke(organizerId.value, id); await loadInvitations() }; confirmDialog.value = true }
async function changePage(amount: number) { page.value += amount; await loadMembers() }
function roleLabel(role: string) { return role === 'EventAdministrator' ? 'Event Administrator' : 'Race Director' }
function formatDate(value: string) { return new Date(value).toLocaleString() }
function notify(message: string, color = 'success') { snackbarText.value = message; snackbarColor.value = color; snackbar.value = true }
function errorMessage(error: unknown) { return isAxiosError(error) ? error.response?.data?.title || error.response?.data?.message || error.message : 'Something went wrong.' }
</script>
