import { ref } from 'vue'
import { scriptsApi } from '@/lib/api'

/** The script pack the server installs (SCRIPTPACKREPO / SCRIPTPACKBRANCH). The
 *  default is shown until the server answers, or if it cannot (setup phase). */
export function useScriptPack() {
  const pack = ref({
    repo: 'Sphereserver/Scripts-X',
    branch: 'main',
    url: 'https://github.com/Sphereserver/Scripts-X',
  })
  scriptsApi.pack().then(({ data }) => { pack.value = data }).catch(() => { /* keep default */ })
  return pack
}
