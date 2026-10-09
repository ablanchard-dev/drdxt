// Chrome miroir : surface souple, reflet de studio tournant, silhouette sphérique.
sampler2D Entree : register(s0);
sampler2D Studio : register(s1);
float Temps : register(c0);
float Luminosite : register(c1);
float4 Teinte : register(c2);
float Taille : register(c3);
float4 main(float2 uv:TEXCOORD):COLOR {
 float2 p=uv*2-1;
 float a=atan2(p.y,p.x);
 float rayon=.955;
 float2 c=p/rayon;
 float d=dot(c,c),alpha=saturate((1-sqrt(d))*Taille*.5);
 if(alpha<=0)return float4(0,0,0,0);
 float z=sqrt(saturate(1-d));
 float2 q=c;
 float torsion=.55*sin(Temps*.28)+1.3*(1-z);
 q=float2(q.x*cos(torsion)-q.y*sin(torsion),q.x*sin(torsion)+q.y*cos(torsion));
 float2 ondulation=float2(sin(q.y*3.8+Temps*.55)+.38*sin(q.x*6-Temps*.38),cos(q.x*3.7-Temps*.48)+.35*cos(q.y*5.5+Temps*.4));
 float3 n=normalize(float3(c+ondulation*.32*z,z));
 float3 r=reflect(float3(0,0,-1),n);
 float turn=Temps*.12;
 r=float3(r.x*cos(turn)+r.z*sin(turn),r.y,-r.x*sin(turn)+r.z*cos(turn));
 float2 env=float2(frac(atan2(r.z,r.x)/6.2831853+.5),acos(clamp(r.y,-1,1))/3.1415926);
 float refletStudio=tex2D(Studio,env).r;
 float ride=r.y+.42*sin(r.x*3.4+Temps*.55)+.18*cos(r.z*4.2-Temps*.4);
 float voile=smoothstep(-.38,-.12,ride)-smoothstep(.08,.34,ride);
 float lisere=exp(-pow((ride+.40)/.026,2));
 float metal=.18+.18*pow(saturate(refletStudio),1.2)+.38*voile+.12*lisere;
 float rim=.10*pow(1-z,5);
 float spec=.28*pow(saturate(dot(n,normalize(float3(-.36,-.52,.82)))),42);
 float value=saturate((metal*(.7+.3*z)+rim+spec)*Luminosite);
 return float4(value*alpha,value*alpha,value*alpha,alpha);
}

