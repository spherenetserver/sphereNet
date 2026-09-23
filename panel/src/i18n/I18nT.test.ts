import { createApp, h, nextTick } from 'vue'
import { describe, expect, it } from 'vitest'
import I18nT from './I18nT'
import { setLocale } from './index'

describe('I18nT', () => {
  it('puts slot content where the placeholder sits, per language', async () => {
    setLocale('en')
    const el = document.createElement('div')
    const app = createApp({
      render: () => h('p', [
        h(I18nT, { k: 'updates.notConfiguredText' }, {
          key: () => h('code', 'APPUPDATEREPO'),
          file: () => h('code', 'config/sphere.ini'),
        }),
      ]),
    })
    app.mount(el)
    expect(el.textContent).toBe('APPUPDATEREPO is empty in config/sphere.ini.')
    expect(el.querySelectorAll('code')).toHaveLength(2)

    setLocale('tr')
    await nextTick()
    // Turkish puts the file first: the order follows the message, not the slots.
    expect(el.textContent).toBe('config/sphere.ini içinde APPUPDATEREPO boş.')
    app.unmount()
  })
})
