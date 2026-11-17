<script setup lang="ts">
import { computed, type Component } from 'vue'
import {
  PhArrowLeft,
  PhBell,
  PhBellSlash,
  PhChatCircleDots,
  PhCheck,
  PhDotsThree,
  PhDownloadSimple,
  PhFileText,
  PhFilePdf,
  PhFileDoc,
  PhFileXls,
  PhFilePpt,
  PhFileZip,
  PhFileAudio,
  PhFileVideo,
  PhFileImage,
  PhGearSix,
  PhImageSquare,
  PhLinkSimple,
  PhMagnifyingGlass,
  PhMicrophone,
  PhPalette,
  PhPaperPlaneTilt,
  PhPaperclip,
  PhPause,
  PhPencilSimple,
  PhPhone,
  PhPlay,
  PhPlus,
  PhPushPin,
  PhShieldCheck,
  PhSignOut,
  PhSmiley,
  PhStar,
  PhUser,
  PhUsersThree,
  PhVideoCamera,
  PhX,
} from '@phosphor-icons/vue'
import type { AppIconName } from '../core/icons'

// 统一图标（UI-D P1）：全站图标收敛到 Phosphor 线性集，保留原 name/size API，
// 既有组件无需改动即可完成第一个自绘组件替换；装饰性图标始终 aria-hidden。
// core/icons.ts 保留 AppIconName 类型与旧图形数据作为历史资产，不再参与渲染。
const ICONS: Record<AppIconName, Component> = {
  chat: PhChatCircleDots,
  contacts: PhUser,
  groups: PhUsersThree,
  settings: PhGearSix,
  search: PhMagnifyingGlass,
  plus: PhPlus,
  image: PhImageSquare,
  file: PhFileText,
  paperclip: PhPaperclip,
  emoji: PhSmiley,
  close: PhX,
  more: PhDotsThree,
  send: PhPaperPlaneTilt,
  download: PhDownloadSimple,
  user: PhUser,
  logout: PhSignOut,
  check: PhCheck,
  back: PhArrowLeft,
  mute: PhBellSlash,
  pin: PhPushPin,
  edit: PhPencilSimple,
  bell: PhBell,
  palette: PhPalette,
  link: PhLinkSimple,
  shield: PhShieldCheck,
  star: PhStar,
  mic: PhMicrophone,
  phone: PhPhone,
  video: PhVideoCamera,
  play: PhPlay,
  pause: PhPause,
  filePdf: PhFilePdf,
  fileDoc: PhFileDoc,
  fileXls: PhFileXls,
  filePpt: PhFilePpt,
  fileZip: PhFileZip,
  fileAudio: PhFileAudio,
  fileVideo: PhFileVideo,
  fileImage: PhFileImage,
}

const props = withDefaults(defineProps<{ name: AppIconName; size?: number }>(), { size: 18 })
const icon = computed(() => ICONS[props.name])
</script>

<template>
  <component
    :is="icon"
    :size="size"
    weight="regular"
    class="ec-icon"
    aria-hidden="true"
    focusable="false"
  />
</template>

<style scoped>
.ec-icon {
  display: block;
  flex: none;
}
</style>
