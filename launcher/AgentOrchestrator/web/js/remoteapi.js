// Stub until the Remote API page lands (phone 2 builds it here): opens #view-remoteapi for a worker.
export async function openRemoteApi(role) {
  const { go } = await import('./main.js');
  go('remoteapi', { role });
}
