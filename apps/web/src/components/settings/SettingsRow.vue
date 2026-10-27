<script setup lang="ts">
// 标准设置行（SET-01）：左侧标题 + 说明，右侧控件。
// - vertical：控件换行显示（输入框、说明、错误提示等需要整行宽度的场景）；
// - clickable：整行可点击（键盘可达、进入子面板/下载页等），否则只渲染为静态行；
// - tone=danger：危险操作（退出登录）用危险色标签。
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
    <span class="ec-set-row__text">
      <span class="ec-set-row__label">{{ label }}</span>
      <span v-if="hint" class="ec-set-row__hint">{{ hint }}</span>
    </span>
    <span class="ec-set-row__control">
      <slot />
    </span>
  </component>
</template>

<style scoped>
.ec-set-row {
  display: flex;
  gap: var(--ec-space-3);
  align-items: center;
  justify-content: space-between;
  width: 100%;
  min-height: 56px;
  padding: var(--ec-space-3) var(--ec-space-4);
  border: none;
  background: transparent;
  color: inherit;
  font: inherit;
  text-align: left;
}

.ec-set-row--vertical {
  flex-direction: column;
  align-items: stretch;
  gap: var(--ec-space-2);
}

.ec-set-row--vertical .ec-set-row__control {
  justify-content: flex-start;
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
