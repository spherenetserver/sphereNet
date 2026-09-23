import { createApp } from 'vue'
import { createPinia } from 'pinia'
import { VueQueryPlugin } from '@tanstack/vue-query'
import App from './App.vue'
import router from './router'
import { initLocale } from './i18n'
import './style.css'
import 'bootstrap-icons/font/bootstrap-icons.css'

const app = createApp(App)
app.use(createPinia())
app.use(router)
app.use(VueQueryPlugin)
initLocale() // sets <html lang> before the first render
app.mount('#app')
