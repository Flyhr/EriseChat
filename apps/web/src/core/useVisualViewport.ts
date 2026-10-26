import { onBeforeUnmount, onMounted, ref } from 'vue'

// UI-04 批次3：Android 软键盘适配（只读，不写业务状态）。
// 监听 window.visualViewport：键盘弹出时可视高度变小，把真实可视高度写入根节点 CSS 变量
// --ec-vvh，外壳高度引用该变量，从而保证输入框/发送区始终在可视区域内、消息列表可滚动。
// 不用 window.resize（部分 WebView 不触发），也不监听 scroll（避免地址栏收缩时反复抖动）。
// 键盘判定：可视高度比初始高度小 120px 以上（区分浏览器地址栏收缩与软键盘弹出）。
const KEYBOARD_THRESHOLD = 120

export function useVisualViewport() {
  const keyboardOpen = ref(false)
  let viewport: VisualViewport | null = null
  let baseHeight = 0

  function apply(): void {
    if (!viewport) return
    const height = Math.round(viewport.height)
    if (height <= 0) return
    if (height > baseHeight) baseHeight = height
    keyboardOpen.value = baseHeight > 0 && baseHeight - height > KEYBOARD_THRESHOLD
    document.documentElement.style.setProperty('--ec-vvh', `${height}px`)
  }

  onMounted(() => {
    viewport = window.visualViewport
    if (!viewport) return
    baseHeight = Math.round(viewport.height)
    apply()
    viewport.addEventListener('resize', apply)
  })

  onBeforeUnmount(() => {
    viewport?.removeEventListener('resize', apply)
    document.documentElement.style.removeProperty('--ec-vvh')
  })

  return { keyboardOpen }
}
