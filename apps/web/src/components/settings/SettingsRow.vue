<script setup lang="ts">
import { NFlex } from 'naive-ui'

// 标准设置行（SET-01）：左侧标题 + 说明，右侧控件。
// - vertical：控件换行显示（输入框、说明、错误提示等需要整行宽度的场景）；
// - clickable：整行可点击（键盘可达、进入子面板/下载页等），否则只渲染为静态行；
// - tone=danger：危险操作（退出登录）用危险色标签。
//
// 主布局改用 Naive UI NFlex（承担 flex-direction / justify / align / gap / wrap）。
// 外层语义节点必须保留：clickable 时要渲染 <button> 保证键盘可达，而 NFlex 恒为 div；
// 页面级 :deep(.ec-set-row + .ec-set-row) 也依赖 .ec-set-row 类名画行分割线。
// NFlex 的 size 只接受数值(px)，下列常量对应 --ec-space-3(12px) / --ec-space-2(8px)。
const GAP_ROW = 12
const GAP_COLUMN = 8

const props = withDefaults(
  defineProps<{
    label: string
    hint?: string
    tone?: 'default' | 'danger'
    vertical?: boolean
    clickable?: boolean
  }>(),
  { tone: 'default', vertical: false, clickable: false },
)

const emit = defineEmits<{ (event: 'activate'): void }>()

function activate(): void {
  if (props.clickable) emit('activate')
}
</script>

<template>
  <component
    :is="clickable ? 'button' : 'div'"
    class="ec-set-row"
    :class="[
      `ec-set-row--${tone}`,
      { 'ec-set-row--vertical': vertical, 'ec-set-row--clickable': clickable },
    ]"
    :type="clickable ? 'button' : undefined"
    @click="activate"
  >
    <NFlex
      class="ec-set-row__layout"
      :vertical="vertical"
      :wrap="false"
      :size="vertical ? GAP_COLUMN : GAP_ROW"
      :align="vertical ? 'stretch' : 'center'"
      justify="space-between"
    >
      <span class="ec-set-row__text">
        <span class="ec-set-row__label">{{ label }}</span>
        <span v-if="hint" class="ec-set-row__hint">{{ hint }}</span>
      </span>
      <span class="ec-set-row__control">
        <slot />
      </span>
    </NFlex>
  </component>
</template>

<style scoped>
.ec-set-row {
  display: flex;
  width: 100%;
  min-height: 56px;
  padding: var(--ec-space-3) var(--ec-space-4);
  border: none;
  background: transparent;
  color: inherit;
  font: inherit;
  text-align: left;
}

/* NFlex 承担主布局；此处只负责撑满行高与横向余量。 */
.ec-set-row__layout {
  flex: 1;
  min-width: 0;
}

.ec-set-row--danger .ec-set-row__label {
  color: var(--ec-danger);
}

.ec-set-row--clickable {
  cursor: pointer;
  transition: background-color var(--ec-transition-fast) var(--ec-ease);
}

@media (hover: hover) and (pointer: fine) {
  .ec-set-row--clickable:hover {
    background: var(--ec-surface-hover);
  }
}

.ec-set-row--clickable:focus-visible {
  outline: var(--ec-focus-width) solid var(--ec-focus-ring);
  outline-offset: calc(-1 * var(--ec-focus-width));
}

.ec-set-row__text {
  display: flex;
  flex: 1;
  flex-direction: column;
  gap: 2px;
  min-width: 0;
}

.ec-set-row__label {
  color: var(--ec-text-primary);
  font-size: var(--ec-font-size-body);
  font-weight: var(--ec-font-weight-medium);
  line-height: var(--ec-line-height-compact);
}

.ec-set-row__hint {
  color: var(--ec-text-secondary);
  font-size: var(--ec-font-size-secondary);
  line-height: 1.45;
}

.ec-set-row__control {
  display: flex;
  flex: none;
  gap: var(--ec-space-2);
  align-items: center;
  justify-content: flex-end;
}

/* 纵向行：控件独占整行并自上而下排列（输入框 + 计数/说明/错误提示）。 */
.ec-set-row--vertical .ec-set-row__control {
  flex: 1;
  flex-direction: column;
  align-items: stretch;
  justify-content: flex-start;
  min-width: 0;
}

@media (max-width: 800px) {
  .ec-set-row {
    min-height: 64px;
    padding: var(--ec-space-3) var(--ec-space-4);
  }

  .ec-set-row__label {
    font-size: 15px;
  }

  .ec-set-row__hint {
    font-size: var(--ec-font-size-mobile-list-sub, 13px);
  }
}
</style>
