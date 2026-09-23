import { Fragment, defineComponent, h, type PropType, type VNodeChild } from 'vue'
import { t, type MessageKey } from './index'

/** Renders a message whose `{name}` placeholders are filled by named slots, so
 *  markup (links, <code>) can sit inside a sentence whose word order differs
 *  between languages. Placeholders without a slot are left as written. */
export default defineComponent({
  name: 'I18nT',
  props: {
    k: { type: String as PropType<MessageKey>, required: true },
  },
  setup(props, { slots }) {
    return () => {
      const parts = t(props.k).split(/(\{\w+\})/)
      const children: VNodeChild[] = parts.map(part => {
        const m = /^\{(\w+)\}$/.exec(part)
        const slot = m ? slots[m[1]] : undefined
        return slot ? slot() : part
      })
      return h(Fragment, null, children as never)
    }
  },
})
