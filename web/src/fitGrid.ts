export function fitGrid(width:number,height:number,rows:number,columns:number,gapLimit=10){
 const r=Math.max(1,rows),c=Math.max(1,columns),w=Math.max(0,width),h=Math.max(0,height);
 const gap=Math.max(0,Math.min(gapLimit,w/(c*12),h/(r*12)));
 const cellWidth=Math.max(0,(w-gap*(c-1))/c),cellHeight=Math.max(0,(h-gap*(r-1))/r);
 const padding=Math.min(8,cellWidth*.08,cellHeight*.08);
 const innerWidth=Math.max(0,cellWidth-padding*2),innerHeight=Math.max(0,cellHeight-padding*2);
 return {gap,cellWidth,cellHeight,padding,
  labelFont:Math.min(15,innerHeight*.25,innerWidth/4),valueFont:Math.min(34,innerHeight*.5,innerWidth/3),
  stateFont:Math.min(12,innerHeight*.22,innerWidth/6),duplicateFont:Math.min(16,innerHeight*.35,innerWidth/5),
  meterHeight:Math.min(4,innerHeight*.05),editorFont:Math.min(16,cellHeight*.45,cellWidth*.3),
  headerFont:Math.min(12,cellHeight*.4,cellWidth*.3),handleSize:Math.min(10,cellWidth*.3,cellHeight*.3)};
}
