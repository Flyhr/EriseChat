// 本地图片加载：把 File/Blob 经 object URL 解码为 HTMLImageElement，用完即释放 object URL。
// 图片压缩（M3-07）与自定义表情（F-21）共用的浏览器侧最小工具；依赖 canvas/Image，不参与纯函数单测。

/** 加载本地图片为 HTMLImageElement；成功或失败都会释放 object URL。 */
export function loadImage(source: Blob): Promise<HTMLImageElement> {
  return new Promise((resolve, reject) => {
    const objectUrl = URL.createObjectURL(source)
    const image = new Image()
    image.onload = () => {
      URL.revokeObjectURL(objectUrl)
      resolve(image)
    }
    image.onerror = () => {
      URL.revokeObjectURL(objectUrl)
      reject(new Error('图片加载失败'))
    }
    image.src = objectUrl
  })
}
